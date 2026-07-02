// -----------------------------------------------------------------------------
//  TotalControl — MCP tool implementations
//
//  Licensed under the MIT License. See LICENSE in the project root for details.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  This file defines the tools exposed by the TotalControl MCP server:
//
//      move_mouse           Move the cursor to absolute screen coordinates.
//      click_mouse          Synthesize a left/right/middle click (single or double).
//      mouse_button         Press OR release a mouse button (low-level drag primitive).
//      drag_mouse           High-level press → move → release drag in one atomic call.
//      scroll_mouse         Scroll the mouse wheel at a given position (vertical or horizontal).
//      send_keys            Type Unicode text + named keys / chords into focus.
//      screenshot_screen    Capture the primary monitor or the full virtual screen.
//      screenshot_window    Capture a specific app window by title or process name.
//      crop_screenshot      Capture a sub-region of the screen or a window (region-of-interest).
//      find_window          List visible top-level windows matching an optional filter — no capture.
//      control_window       Pin a window top-most + draw an "Under Agent Control" frame.
//      release_window       Remove the frame and restore a controlled window's state.
//      record_window        Screen-record a controlled window to an MP4 (H.264, via FFmpeg).
//      click_in_window      Click at WINDOW-relative coords — server does the math.
//      hover_preview        Move cursor, return a small crop with crosshair overlay.
//      find_element         UI Automation tree walk; locate (and optionally invoke) a control.
//
//  Every tool carries a [Description(...)] attribute. That text is what the
//  agent sees when deciding which tool to call, so the descriptions are
//  intentionally verbose — they include intent, syntax, examples, and the
//  recommended see → act → verify loop.
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
// Disambiguate: WPF's System.Windows namespace defines its own Point / Size /
// Rectangle / Color types that would otherwise collide with System.Drawing.
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Rectangle = System.Drawing.Rectangle;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;
using Bitmap = System.Drawing.Bitmap;
using InterpolationMode = System.Drawing.Drawing2D.InterpolationMode;

namespace TotalControl;

[McpServerToolType]
public static class DesktopTools
{
    // -----------------------------------------------------------------------
    //  Process-name cache
    //  EnumerateTopLevelWindows previously called Process.GetProcessById for
    //  every visible window on every tool call — one kernel call per window.
    //  A short-TTL static cache reduces that to one lookup per unique PID
    //  per 10-second window (processes don't normally start/exit that fast).
    // -----------------------------------------------------------------------
    private sealed record ProcEntry(string Name, long ExpiresAt);
    private static readonly ConcurrentDictionary<uint, ProcEntry> ProcessNameCache = new();
    private const long ProcessCacheTtl = 10 * TimeSpan.TicksPerSecond;

    private static string GetProcessName(uint pid)
    {
        long now = DateTime.UtcNow.Ticks;
        if (ProcessNameCache.TryGetValue(pid, out var entry) && entry.ExpiresAt > now)
            return entry.Name;
        string name;
        try { using var pr = Process.GetProcessById((int)pid); name = pr.ProcessName; }
        catch { name = "?"; }
        ProcessNameCache[pid] = new ProcEntry(name, now + ProcessCacheTtl);
        return name;
    }
    // ============================================================
    //  MOUSE
    // ============================================================

    [McpServerTool(Name = "move_mouse")]
    [Description(
        "Moves the system mouse cursor to absolute screen coordinates in PHYSICAL PIXELS.\n" +
        "Origin (0,0) is the top-left of the PRIMARY monitor. Negative X/Y are valid on multi-monitor " +
        "setups where secondary displays sit left of or above the primary.\n" +
        "Use this BEFORE 'click_mouse' when you want a deliberate two-step (move, verify visually with " +
        "'screenshot_screen', then click). For a single fused 'go there and click' action, pass x/y " +
        "directly to 'click_mouse' instead — it is one atomic operation and faster.\n" +
        "Returns a confirmation string with the resulting cursor position.")]
    public static string MoveMouse(
        [Description("Target X coordinate in physical pixels. 0 = left edge of primary monitor. Range is bounded by the virtual screen (all monitors combined).")]
        int x,
        [Description("Target Y coordinate in physical pixels. 0 = top edge of primary monitor.")]
        int y)
    {
        if (!Win32.SetCursorPos(x, y))
            throw new InvalidOperationException($"SetCursorPos({x},{y}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        Win32.GetCursorPos(out var p);
        return $"Cursor at ({p.X}, {p.Y}).";
    }

    [McpServerTool(Name = "click_mouse")]
    [Description(
        "Synthesizes a hardware-level mouse click using SendInput. Apps cannot detect that the click was " +
        "automated.\n" +
        "Behavior:\n" +
        "  • If x and y are provided, the cursor first moves there, then clicks (atomic — preferred).\n" +
        "  • If x/y are omitted, the click happens at the CURRENT cursor position.\n" +
        "  • 'count' = 2 produces a real double-click (paired down/up/down/up within the system " +
        "double-click time), not two separate clicks.\n" +
        "Common patterns:\n" +
        "  • Single left click at (x,y):   button='left',  x=…, y=…\n" +
        "  • Double-click to open a file:  button='left',  count=2\n" +
        "  • Right-click context menu:     button='right'\n" +
        "After clicking a control that takes focus (e.g. a text box), follow with 'send_keys' to type.")]
    public static string ClickMouse(
        [Description("Mouse button to click. Allowed: 'left', 'right', 'middle'. Default 'left'.")]
        string button = "left",
        [Description("Optional X coordinate (physical pixels). If provided together with y, the cursor moves there before clicking. Omit to click in place.")]
        int? x = null,
        [Description("Optional Y coordinate (physical pixels). Must be provided together with x.")]
        int? y = null,
        [Description("Number of consecutive clicks. 1 = single click (default). 2 = double-click. Range 1–5.")]
        int count = 1)
    {
        if (count < 1 || count > 5) throw new ArgumentOutOfRangeException(nameof(count), "count must be 1..5.");
        if ((x is null) ^ (y is null)) throw new ArgumentException("x and y must be provided together.");

        var gate = Enforcement.GateAny("click_mouse");
        if (gate is not null) return gate;

        if (x is int xi && y is int yi)
        {
            if (!Win32.SetCursorPos(xi, yi))
                throw new InvalidOperationException($"SetCursorPos({xi},{yi}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var (down, up) = button.ToLowerInvariant() switch
        {
            "left"   => (Win32.MouseEventF.LeftDown,   Win32.MouseEventF.LeftUp),
            "right"  => (Win32.MouseEventF.RightDown,  Win32.MouseEventF.RightUp),
            "middle" => (Win32.MouseEventF.MiddleDown, Win32.MouseEventF.MiddleUp),
            _ => throw new ArgumentException($"Unknown button '{button}'. Use 'left', 'right' or 'middle'."),
        };

        var inputs = new List<Win32.INPUT>(count * 2);
        for (int i = 0; i < count; i++)
        {
            inputs.Add(MouseEvent(down));
            inputs.Add(MouseEvent(up));
        }
        SendInputs(inputs);

        Win32.GetCursorPos(out var p);
        return count == 1
            ? $"{button} click at ({p.X}, {p.Y})."
            : $"{button} click ×{count} at ({p.X}, {p.Y}).";
    }

    [McpServerTool(Name = "mouse_button")]
    [Description(
        "Low-level mouse-button primitive: presses ('down') OR releases ('up') a single button — " +
        "WITHOUT a matching opposite event. This is the building block for drag-and-drop, marquee " +
        "selection, paint-stroke gestures, slider drags, and any other interaction that needs the button " +
        "held while the cursor moves.\n" +
        "For a normal click, prefer 'click_mouse' (it pairs down+up atomically). Reach for 'mouse_button' " +
        "only when you need to separate the press from the release.\n" +
        "Standard drag pattern:\n" +
        "  1. move_mouse   x=startX  y=startY        ← position over the drag handle\n" +
        "  2. mouse_button action='down'             ← grab\n" +
        "  3. move_mouse   x=endX    y=endY          ← drag\n" +
        "  4. mouse_button action='up'               ← drop\n" +
        "For simple drags, prefer the one-shot 'drag_mouse' tool — it emits proper WM_MOUSEMOVE events " +
        "between the press and release so apps that gate on DragDetect (Explorer, most editors) recognize " +
        "the gesture.\n" +
        "If you call 'down' you MUST eventually call 'up' for the same button — a leaked button-down " +
        "leaves the system in a broken state until the user clicks manually.")]
    public static string MouseButton(
        [Description("Either 'down' (press and hold) or 'up' (release a previously-held button). Required.")]
        string action,
        [Description("Mouse button. Allowed: 'left', 'right', 'middle'. Default 'left'.")]
        string button = "left",
        [Description("Optional X coordinate (physical pixels). If provided together with y, the cursor moves there before the press/release. Omit to act at the current cursor position.")]
        int? x = null,
        [Description("Optional Y coordinate (physical pixels). Must be provided together with x.")]
        int? y = null)
    {
        if ((x is null) ^ (y is null)) throw new ArgumentException("x and y must be provided together.");

        var gate = Enforcement.GateAny("mouse_button");
        if (gate is not null) return gate;

        var act = action?.ToLowerInvariant();
        bool isDown = act switch
        {
            "down" or "press" or "hold" => true,
            "up" or "release"           => false,
            _ => throw new ArgumentException($"Unknown action '{action}'. Use 'down' or 'up'."),
        };

        if (x is int xi && y is int yi && !Win32.SetCursorPos(xi, yi))
            throw new InvalidOperationException($"SetCursorPos({xi},{yi}) failed (Win32 error {Marshal.GetLastWin32Error()}).");

        var flag = (button.ToLowerInvariant(), isDown) switch
        {
            ("left",   true)  => Win32.MouseEventF.LeftDown,
            ("left",   false) => Win32.MouseEventF.LeftUp,
            ("right",  true)  => Win32.MouseEventF.RightDown,
            ("right",  false) => Win32.MouseEventF.RightUp,
            ("middle", true)  => Win32.MouseEventF.MiddleDown,
            ("middle", false) => Win32.MouseEventF.MiddleUp,
            _ => throw new ArgumentException($"Unknown button '{button}'. Use 'left', 'right' or 'middle'."),
        };

        SendInputs(new[] { MouseEvent(flag) });
        Win32.GetCursorPos(out var p);
        return $"{button} {(isDown ? "down" : "up")} at ({p.X}, {p.Y}).";
    }

    [McpServerTool(Name = "drag_mouse")]
    [Description(
        "Performs an atomic mouse drag: press button at (startX, startY) → glide through intermediate " +
        "points to (endX, endY) → release. Use for drag-and-drop, marquee/lasso selection, slider " +
        "manipulation, painting strokes, reordering list items, or any gesture that needs the button held " +
        "while the cursor moves.\n" +
        "Why this exists vs raw 'mouse_button down → move_mouse → mouse_button up': many apps gate drag " +
        "detection on DragDetect, which requires real WM_MOUSEMOVE events between down and up. This tool " +
        "sends those move events at hardware level (SendInput with MOUSEEVENTF_MOVE/ABSOLUTE/VIRTUALDESK), " +
        "so Explorer, file managers, design tools, web pages, and IDE tabs all recognize the gesture.\n" +
        "Tuning:\n" +
        "  • 'downDelayMs' (default 20) — pause AFTER the button-down BEFORE the first move event. " +
        "Required by DragDetect-based apps: they wait for at least one timer tick between down and the " +
        "first move before classifying the gesture as a drag. Bump to 50–100 for stubborn apps.\n" +
        "  • 'steps' (default 20) — number of WM_MOUSEMOVE events along the path. By default all steps " +
        "are batched into one SendInput call (fastest). Set stepDelayMs > 0 to spread them out instead.\n" +
        "  • 'stepDelayMs' (default 0) — ms between successive move events when > 0. Set to 8–16 for " +
        "slow, observable drags or apps that animate step-by-step.\n" +
        "  • 'holdAfterMs' (default 30) — pause between the last move and button release. A few apps " +
        "need ~50–200 ms here to register the drop.\n" +
        "Returns the resulting cursor position and path summary.")]
    public static string DragMouse(
        [Description("X coordinate of the drag START in physical pixels.")] int startX,
        [Description("Y coordinate of the drag START in physical pixels.")] int startY,
        [Description("X coordinate of the drag END in physical pixels.")]   int endX,
        [Description("Y coordinate of the drag END in physical pixels.")]   int endY,
        [Description("Mouse button to hold during the drag. Allowed: 'left', 'right', 'middle'. Default 'left'.")]
        string button = "left",
        [Description("Number of intermediate WM_MOUSEMOVE events along the path. Default 20. Range 1..1000.")]
        int steps = 20,
        [Description("Milliseconds to wait between successive move events. 0 = batch all moves in one SendInput call (fastest, default). Range 0..1000.")]
        int stepDelayMs = 0,
        [Description("Milliseconds to pause after the final move, before releasing the button. Default 30. Range 0..5000.")]
        int holdAfterMs = 30,
        [Description("Milliseconds to pause AFTER button-down BEFORE the first move event. Default 20. DragDetect-based apps require at least one tick between down and first move. Bump to 50–100 for stubborn targets. Range 0..5000.")]
        int downDelayMs = 20)
    {
        if (steps        < 1    || steps        > 1000) throw new ArgumentOutOfRangeException(nameof(steps),        "steps must be 1..1000.");
        if (stepDelayMs  < 0    || stepDelayMs  > 1000) throw new ArgumentOutOfRangeException(nameof(stepDelayMs),  "stepDelayMs must be 0..1000.");
        if (holdAfterMs  < 0    || holdAfterMs  > 5000) throw new ArgumentOutOfRangeException(nameof(holdAfterMs),  "holdAfterMs must be 0..5000.");
        if (downDelayMs  < 0    || downDelayMs  > 5000) throw new ArgumentOutOfRangeException(nameof(downDelayMs),  "downDelayMs must be 0..5000.");

        var gate = Enforcement.GateAny("drag_mouse");
        if (gate is not null) return gate;

        var (down, up) = button.ToLowerInvariant() switch
        {
            "left"   => (Win32.MouseEventF.LeftDown,   Win32.MouseEventF.LeftUp),
            "right"  => (Win32.MouseEventF.RightDown,  Win32.MouseEventF.RightUp),
            "middle" => (Win32.MouseEventF.MiddleDown, Win32.MouseEventF.MiddleUp),
            _ => throw new ArgumentException($"Unknown button '{button}'. Use 'left', 'right' or 'middle'."),
        };

        // Cache virtual screen bounds ONCE — AbsoluteMoveEventRaw uses these on every step,
        // so calling GetSystemMetrics 4× per step (as the old single-event path did) is wasteful.
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));

        // 1. Park the cursor at the start position.
        if (!Win32.SetCursorPos(startX, startY))
            throw new InvalidOperationException($"SetCursorPos({startX},{startY}) failed (Win32 error {Marshal.GetLastWin32Error()}).");

        // 2. Absolute-move to start + button-down in a single SendInput call.
        //    Sending the move event ensures apps that track cursor position before
        //    the click (hit-testing, hover highlighting) see a clean entry event.
        SendInputs(new[]
        {
            AbsoluteMoveEventRaw(startX, startY, vx, vy, vw, vh),
            MouseEvent(down),
        });

        // 3. Pause after button-down so DragDetect-based apps (Explorer, Office, …)
        //    have time to arm the drag threshold before the first WM_MOUSEMOVE arrives.
        if (downDelayMs > 0) Thread.Sleep(downDelayMs);

        // 4. Build all intermediate move events.
        var moveEvents = new Win32.INPUT[steps];
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            int x = (int)Math.Round(startX + (endX - startX) * t);
            int y = (int)Math.Round(startY + (endY - startY) * t);
            moveEvents[i - 1] = AbsoluteMoveEventRaw(x, y, vx, vy, vw, vh);
        }

        if (stepDelayMs > 0)
        {
            // Slow/observable mode: one OS call per step with a delay in between.
            foreach (var ev in moveEvents)
            {
                SendInputs(new[] { ev });
                Thread.Sleep(stepDelayMs);
            }
        }
        else
        {
            // Fast mode (default): all move events in ONE SendInput call.
            // The OS enqueues them atomically — no other thread can sneak events
            // in between, and we halve the number of user→kernel transitions.
            SendInputs(moveEvents);
        }

        // 5. Snap cursor exactly to the endpoint before releasing the button.
        //    Normalization rounding in AbsoluteMoveEventRaw can be off by ±1 pixel;
        //    SetCursorPos gives us a byte-perfect landing position.
        Win32.SetCursorPos(endX, endY);
        if (holdAfterMs > 0) Thread.Sleep(holdAfterMs);

        // 6. Release.
        SendInputs(new[] { MouseEvent(up) });

        Win32.GetCursorPos(out var p);
        return $"{button} drag ({startX},{startY}) → ({endX},{endY}) over {steps} step(s); cursor now at ({p.X}, {p.Y}).";
    }

    [McpServerTool(Name = "scroll_mouse")]
    [Description(
        "Scrolls the mouse wheel at the current cursor position (or at an optional (x, y) position).\n" +
        "Use for: scrolling web pages, lists, code editors, zooming canvas areas, navigating menus.\n" +
        "Parameters:\n" +
        "  • 'amount' — number of scroll 'clicks'. Positive = scroll UP (or RIGHT for horizontal). " +
        "Negative = scroll DOWN (or LEFT). One click = 120 raw delta units (one WHEEL_DELTA notch). " +
        "Typical values: 3 (three notches up) or -5 (five notches down). Default 3.\n" +
        "  • 'horizontal' — false (default) scrolls the vertical wheel; true scrolls horizontal.\n" +
        "  • 'x', 'y' — optional screen coordinates. If supplied, the cursor moves there first.")]
    public static string ScrollMouse(
        [Description("Number of wheel notches. Positive = up/right. Negative = down/left. Default 3. Range -50..50.")]
        int amount = 3,
        [Description("If true, scroll horizontally (left/right). Default false (vertical).")]
        bool horizontal = false,
        [Description("Optional X coordinate (physical pixels) to move to before scrolling. Omit to scroll in place.")]
        int? x = null,
        [Description("Optional Y coordinate. Must be provided together with x.")]
        int? y = null)
    {
        if (amount < -50 || amount > 50) throw new ArgumentOutOfRangeException(nameof(amount), "amount must be -50..50.");
        if (x.HasValue != y.HasValue) throw new ArgumentException("x and y must be provided together.");

        var gate = Enforcement.GateAny("scroll_mouse");
        if (gate is not null) return gate;

        if (x.HasValue && y.HasValue)
        {
            if (!Win32.SetCursorPos(x.Value, y.Value))
                throw new InvalidOperationException($"SetCursorPos({x},{y}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        // Each SendInput wheel event carries one WHEEL_DELTA (120). We batch all notches
        // into a single call so they arrive as one uninterrupted burst.
        int delta = amount * 120; // total raw delta; OS divides back into notches for the app
        var flag = horizontal ? Win32.MouseEventF.HWheel : Win32.MouseEventF.Wheel;

        // mouseData carries the signed delta but the INPUT struct field is uint — cast via int.
        SendInputs(new[]
        {
            new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                U = new Win32.INPUTUNION
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)delta),
                        dwFlags   = (uint)flag,
                    }
                }
            }
        });

        Win32.GetCursorPos(out var p);
        var dir = horizontal ? (amount > 0 ? "right" : "left") : (amount > 0 ? "up" : "down");
        return $"Scrolled {Math.Abs(amount)} notch(es) {dir} at cursor ({p.X},{p.Y}).";
    }

    // ============================================================
    //  KEYBOARD
    // ============================================================

    [McpServerTool(Name = "send_keys")]
    [Description(
        "Sends keystrokes to whichever window currently has keyboard focus. Uses SendInput, so apps " +
        "cannot tell it was automated.\n" +
        "MAKE SURE THE TARGET WINDOW IS FOCUSED FIRST — either click it with 'click_mouse', or type " +
        "{Alt+Tab} / {Win} to switch.\n" +
        "Syntax for the 'keys' parameter:\n" +
        "  • Plain text → typed as Unicode (works with any IME, no shift-state guessing).\n" +
        "      'Hello, world!'\n" +
        "  • Named keys → wrap in braces. Case-insensitive:\n" +
        "      {Enter} {Tab} {Esc} {Space} {Backspace} {Delete}\n" +
        "      {Up} {Down} {Left} {Right} {Home} {End} {PageUp} {PageDown} {Insert}\n" +
        "      {F1}..{F24}  {Win}  {Apps}  {CapsLock}  {NumLock}\n" +
        "      {VolumeUp} {VolumeDown} {VolumeMute} {Play} {NextTrack} {PrevTrack}\n" +
        "  • Modifier chords → '+' inside braces:\n" +
        "      {Ctrl+S}   {Ctrl+Shift+T}   {Alt+F4}   {Win+R}   {Win+D}\n" +
        "  • Repeat → trailing count: '{Tab 5}' (Tab five times), '{Backspace 10}'.\n" +
        "  • Literal braces → '{{' and '}}'.\n" +
        "Examples:\n" +
        "  'Save the file{Ctrl+S}'\n" +
        "  '{Win+R}notepad{Enter}'\n" +
        "  '{Ctrl+A}{Delete}New content here.{Enter}'\n" +
        "Returns the count of synthesized key events.")]
    public static string SendKeys(
        [Description("Key sequence string. Plain Unicode text plus optional {Key}, {Mod+Key}, {Key N}. See tool description for full syntax.")]
        string keys)
    {
        if (string.IsNullOrEmpty(keys)) return "0 keystrokes sent.";

        var gate = Enforcement.GateAny("send_keys");
        if (gate is not null) return gate;

        var inputs = KeySequenceParser.Parse(keys);
        SendInputs(inputs);
        return $"{inputs.Count} key event(s) sent.";
    }

    // ============================================================
    //  SCREENSHOTS
    // ============================================================

    [McpServerTool(Name = "screenshot_screen")]
    [Description(
        "Captures a PNG (or JPEG) screenshot of the desktop and returns it INLINE as an image content " +
        "block so you can visually reason about the current screen state.\n" +
        "Default behavior: captures the PRIMARY monitor only (most efficient).\n" +
        "Set 'allMonitors=true' to capture the full virtual screen across every connected display.\n" +
        "PERFORMANCE TIPS — reduce token cost when full resolution isn't needed:\n" +
        "  • 'scale=0.5' halves both dimensions (quarter of the pixels, ~4× smaller file).\n" +
        "  • 'jpegQuality=75' switches to JPEG encoding (~10–20× smaller than PNG for typical UIs).\n" +
        "  • Combine both: scale=0.5 jpegQuality=75 for maximum speed when just navigating.\n" +
        "  • Use 'crop_screenshot' to capture only the region of interest.\n" +
        "USE THIS BEFORE EVERY DECISION that depends on what is on screen. Standard loop:\n" +
        "screenshot → reason → act → screenshot to verify.\n" +
        "Returns a text block with capture metadata (resolution, format, file size) plus the image.")]
    public static IList<ContentBlock> ScreenshotScreen(
        [Description("If true, capture the entire virtual screen across all monitors. Default false (primary monitor only).")]
        bool allMonitors = false,
        [Description("Downsample factor before encoding. 1.0 = full resolution (default). 0.5 = half width/height (~4× smaller). Range 0.1..1.0.")]
        float scale = 1f,
        [Description("JPEG quality 0–100. -1 (default) = lossless PNG. 75 gives good quality at ~15× smaller than PNG. 50 is acceptable for navigation shots.")]
        int jpegQuality = -1)
    {
        var gate = Enforcement.GateAny("screenshot_screen");
        if (gate is not null) return new List<ContentBlock> { new TextContentBlock { Text = gate } };

        Rectangle bounds = allMonitors
            ? new Rectangle(
                Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN),
                Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN),
                Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN),
                Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN))
            : new Rectangle(0, 0,
                Win32.GetSystemMetrics(Win32.SM_CXSCREEN),
                Win32.GetSystemMetrics(Win32.SM_CYSCREEN));

        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

        return BuildImageResult(bmp,
            $"Captured {(allMonitors ? "virtual screen" : "primary monitor")} {bounds.Width}×{bounds.Height} at ({bounds.X},{bounds.Y}).",
            scale, jpegQuality);
    }

    [McpServerTool(Name = "screenshot_window")]
    [Description(
        "Captures a PNG (or JPEG) screenshot of a SPECIFIC application window — even if it is partially " +
        "or fully covered by other windows or minimized. Returns the image inline.\n" +
        "Lookup strategy (tried in order against the supplied 'query'):\n" +
        "  1. Exact window-title match (case-insensitive)\n" +
        "  2. Exact process-name match (e.g. 'notepad', 'chrome' — no .exe needed)\n" +
        "  3. Substring window-title match\n" +
        "  4. Substring process-name match\n" +
        "If the query is ambiguous, the tool returns a list of candidate windows (title + process + PID).\n" +
        "PERFORMANCE TIPS:\n" +
        "  • 'scale=0.5' halves both dimensions (~4× smaller file). Use for navigation / orientation.\n" +
        "  • 'jpegQuality=75' gives good quality at ~15× smaller than PNG. Use when you need to read text.\n" +
        "  • Use 'crop_screenshot' with window=query to capture a sub-region of the window.\n" +
        "Capture method: PrintWindow with PW_RENDERFULLCONTENT, which works for Win32, WPF, UWP, " +
        "Chromium/Electron and WebView2 surfaces. Minimized windows are temporarily restored.")]
    public static IList<ContentBlock> ScreenshotWindow(
        [Description("Window title or process name to find. Tried in order: exact title → exact process → substring title → substring process. Examples: 'Calculator', 'Save As', 'notepad', 'Visual Studio Code'.")]
        string query,
        [Description("If true, also bring the window to the foreground after capture. Default false.")]
        bool activate = false,
        [Description("Downsample factor before encoding. 1.0 = full resolution (default). 0.5 = half. Range 0.1..1.0.")]
        float scale = 1f,
        [Description("JPEG quality 0–100. -1 (default) = lossless PNG. 75 = good quality, ~15× smaller.")]
        int jpegQuality = -1)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.");
        var windows = EnumerateTopLevelWindows();
        var match = FindWindow(windows, query);
        if (match is null)
        {
            var candidates = windows
                .Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || w.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();
            var msg = new StringBuilder();
            msg.AppendLine($"No window matched '{query}'.");
            if (candidates.Count > 0)
            {
                msg.AppendLine("Candidates:");
                foreach (var c in candidates) msg.AppendLine($"  • '{c.Title}' [{c.ProcessName}.exe, PID {c.Pid}]");
            }
            else
            {
                msg.AppendLine("Top visible windows:");
                foreach (var w in windows.Take(15)) msg.AppendLine($"  • '{w.Title}' [{w.ProcessName}.exe, PID {w.Pid}]");
            }
            return new List<ContentBlock> { new TextContentBlock { Text = msg.ToString() } };
        }

        var gate = Enforcement.GateTarget(WindowControl.IsControlled(match.Hwnd), match.Title, "screenshot_window");
        if (gate is not null) return new List<ContentBlock> { new TextContentBlock { Text = gate } };

        if (Win32.IsIconic(match.Hwnd)) Win32.ShowWindow(match.Hwnd, Win32.SW_RESTORE);

        // DWM extended frame bounds give the true rect (excludes invisible resize border on Win10+)
        if (Win32.DwmGetWindowAttribute(match.Hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<Win32.RECT>()) != 0
            || rect.Width <= 0 || rect.Height <= 0)
        {
            Win32.GetWindowRect(match.Hwnd, out rect);
        }
        int w0 = Math.Max(1, rect.Width), h0 = Math.Max(1, rect.Height);

        using var bmp = new Bitmap(w0, h0, PixelFormat.Format32bppArgb);
        bool printOk;
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                printOk = Win32.PrintWindow(match.Hwnd, hdc, Win32.PW_RENDERFULLCONTENT);
            }
            finally { g.ReleaseHdc(hdc); }
        }

        // Fallback: many full-screen / DirectX / protected surfaces refuse PrintWindow → use desktop copy.
        if (!printOk)
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(new Point(rect.Left, rect.Top), Point.Empty, new Size(w0, h0), CopyPixelOperation.SourceCopy);
        }

        if (activate) Win32.SetForegroundWindow(match.Hwnd);

        var meta = $"Captured window '{match.Title}' [{match.ProcessName}.exe, PID {match.Pid}] — {w0}×{h0} at ({rect.Left},{rect.Top}). Method={(printOk ? "PrintWindow" : "ScreenCopy")}.";
        return BuildImageResult(bmp, meta, scale, jpegQuality);
    }

    [McpServerTool(Name = "crop_screenshot")]
    [Description(
        "Captures a rectangular SUB-REGION of the screen or a specific window and returns it inline.\n" +
        "This is the #1 tool for reducing token cost: instead of sending a full 2256×1504 screenshot, " +
        "send only the 300×200 region that actually contains the button / dialog / element you care about.\n" +
        "Coordinate modes:\n" +
        "  • 'window' not set → x, y are absolute SCREEN coordinates (physical pixels).\n" +
        "  • 'window' set → x, y are WINDOW-RELATIVE coordinates (same origin as 'screenshot_window' PNG).\n" +
        "    The tool resolves the window, reads its DWM frame origin, and adds (x,y) to it.\n" +
        "Typical workflow:\n" +
        "  1. screenshot_window scale=0.5 to orient yourself.\n" +
        "  2. Identify the region of interest (e.g. the confirmation dialog at window-coords 200,150).\n" +
        "  3. crop_screenshot window='MyApp' x=200 y=150 width=400 height=200 to read it at full res.\n" +
        "Returns a text block with origin, size, format metadata and the image.")]
    public static IList<ContentBlock> CropScreenshot(
        [Description("Left edge of the crop rectangle. Screen coordinates if 'window' is not set; window-relative otherwise.")]
        int x,
        [Description("Top edge of the crop rectangle.")]
        int y,
        [Description("Width of the crop rectangle in pixels. Range 1..8000.")]
        int width,
        [Description("Height of the crop rectangle in pixels. Range 1..8000.")]
        int height,
        [Description("Optional window title or process name. When set, x/y are window-relative coordinates (matching 'screenshot_window' PNG origin). When omitted, x/y are absolute screen coordinates.")]
        string? window = null,
        [Description("Downsample factor before encoding. 1.0 = full resolution (default). 0.5 = half. Range 0.1..1.0.")]
        float scale = 1f,
        [Description("JPEG quality 0–100. -1 (default) = lossless PNG. 75 = good quality, much smaller.")]
        int jpegQuality = -1)
    {
        if (width  < 1 || width  > 8000) throw new ArgumentOutOfRangeException(nameof(width),  "width must be 1..8000.");
        if (height < 1 || height > 8000) throw new ArgumentOutOfRangeException(nameof(height), "height must be 1..8000.");

        // Resolve window-relative → absolute screen coords if a window is specified.
        int screenX = x, screenY = y;
        string originLabel;
        if (!string.IsNullOrWhiteSpace(window))
        {
            var match = ResolveWindowOrThrow(window);

            var gate = Enforcement.GateTarget(WindowControl.IsControlled(match.Hwnd), match.Title, "crop_screenshot");
            if (gate is not null) return new List<ContentBlock> { new TextContentBlock { Text = gate } };

            if (Win32.DwmGetWindowAttribute(match.Hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var wr, Marshal.SizeOf<Win32.RECT>()) != 0
                || wr.Width <= 0)
            {
                Win32.GetWindowRect(match.Hwnd, out wr);
            }
            screenX = wr.Left + x;
            screenY = wr.Top  + y;
            originLabel = $"window '{match.Title}' origin ({wr.Left},{wr.Top}) + ({x},{y})";
        }
        else
        {
            var gate = Enforcement.GateAny("crop_screenshot");
            if (gate is not null) return new List<ContentBlock> { new TextContentBlock { Text = gate } };

            originLabel = $"screen ({x},{y})";
        }

        // Clamp to virtual screen bounds so we never try to capture off-screen pixels.
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        int cx = Math.Clamp(screenX, vx, vx + vw - 1);
        int cy = Math.Clamp(screenY, vy, vy + vh - 1);
        int cw = Math.Min(width,  vx + vw - cx);
        int ch = Math.Min(height, vy + vh - cy);
        if (cw < 1 || ch < 1) throw new ArgumentException($"Crop rect ({screenX},{screenY} {width}×{height}) is entirely outside the virtual screen bounds.");

        using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(cx, cy, 0, 0, new Size(cw, ch), CopyPixelOperation.SourceCopy);

        return BuildImageResult(bmp,
            $"Crop {cw}×{ch} at {originLabel}.",
            scale, jpegQuality);
    }

    [McpServerTool(Name = "find_window")]
    [Description(
        "Lists visible top-level windows WITHOUT capturing any pixels. Returns title, process, PID, " +
        "and screen position for each match.\n" +
        "Use this to:\n" +
        "  • Orient yourself before a screenshot ('what windows are open right now?').\n" +
        "  • Verify a window exists before trying to capture or click it.\n" +
        "  • Find the exact title to pass to 'screenshot_window' or 'click_in_window'.\n" +
        "  • List all running apps quickly (no image tokens consumed).\n" +
        "Supply 'filter' to narrow results; omit it to list everything.\n" +
        "Returns a formatted text list — no image payload.")]
    public static string FindWindow(
        [Description("Optional substring filter applied to window title AND process name (case-insensitive). Omit to list ALL visible windows.")]
        string? filter = null,
        [Description("Maximum number of windows to return. Default 30. Range 1..200.")]
        int maxResults = 30)
    {
        if (maxResults < 1 || maxResults > 200) throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be 1..200.");

        var windows = EnumerateTopLevelWindows();
        IEnumerable<WindowInfo> matches = windows;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = windows.Where(w =>
                w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        var list = matches.Take(maxResults).ToList();

        if (list.Count == 0)
        {
            return string.IsNullOrWhiteSpace(filter)
                ? "No visible windows found."
                : $"No windows matched filter '{filter}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(filter)
            ? $"{list.Count} visible window(s){(windows.Count > maxResults ? $" (showing first {maxResults} of {windows.Count})" : "")}:"
            : $"{list.Count} window(s) matching '{filter}'{(list.Count == maxResults ? $" (limit {maxResults})" : "")}:");

        foreach (var w in list)
        {
            string sizeStr = "";
            try
            {
                if (Win32.DwmGetWindowAttribute(w.Hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Win32.RECT>()) == 0 && r.Width > 0)
                    sizeStr = $" {r.Width}×{r.Height} at ({r.Left},{r.Top})";
            }
            catch { /* non-critical */ }
            sb.AppendLine($"  • '{w.Title}' [{w.ProcessName}.exe, PID {w.Pid}]{sizeStr}");
        }
        return sb.ToString();
    }

    // ============================================================
    //  WINDOW CONTROL (top-most + "Under Agent Control" frame)
    // ============================================================

    [McpServerTool(Name = "control_window")]
    [Description(
        "Takes exclusive visual control of ONE window: makes it TOP-MOST (stays above all other windows), " +
        "brings it to the foreground, and draws a crimson border around it plus an 'Under Agent Control' " +
        "tag with an ✕ button above its titlebar.\n" +
        "WHY: agents frequently mis-click because the target window is in the background and another window " +
        "is actually on top at those coordinates. Calling control_window FIRST guarantees the window you " +
        "screenshot and click is the one on top, and shows the user exactly which window you are driving.\n" +
        "RECOMMENDED WORKFLOW:\n" +
        "  1. control_window query='Calculator'   ← pin it on top, frame it.\n" +
        "  2. screenshot_window / click_in_window / send_keys …  ← do your work.\n" +
        "  3. release_window query='Calculator'    ← YOU MUST DO THIS when finished.\n" +
        "IMPORTANT — YOU MUST RELEASE: the window stays permanently top-most until you call " +
        "'release_window' (or the user clicks the ✕ on the tag). Always release when you are done with a " +
        "window. The frame follows the window as it moves/resizes and hides while it is minimized.\n" +
        "Window lookup uses the same four-pass match as screenshot_window (exact title → exact process → " +
        "substring title → substring process).")]
    public static string ControlWindow(
        [Description("Window title or process name to take control of. Examples: 'Calculator', 'Notepad', 'Visual Studio Code'.")]
        string query)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.");

        var abort = Enforcement.CheckAbort();
        if (abort is not null) return abort;

        var match = ResolveWindowOrThrow(query);
        WindowControl.Acquire(match.Hwnd, match.Title);
        return $"Window '{match.Title}' [{match.ProcessName}.exe, PID {match.Pid}] is now UNDER AGENT CONTROL: " +
               $"set top-most, brought to front, crimson frame + 'Under Agent Control' tag shown.\n" +
               $"⚠️ YOU MUST call release_window query='{match.Title}' (or all=true) when you finish — " +
               $"otherwise it stays top-most permanently.";
    }

    [McpServerTool(Name = "release_window")]
    [Description(
        "Releases a window previously taken with 'control_window': removes the crimson frame + tag and " +
        "restores the window's original top-most state (i.e. reverts it to NOT top-most unless it already " +
        "was top-most before you controlled it).\n" +
        "Call this the moment you are finished working with a controlled window. Pass 'all=true' to release " +
        "every controlled window at once (do this before ending a task to be safe).")]
    public static string ReleaseWindow(
        [Description("Window title or process name to release. Optional if 'all' is true.")]
        string? query = null,
        [Description("If true, release ALL windows currently under agent control. Default false.")]
        bool all = false)
    {
        if (all)
        {
            int n = WindowControl.ReleaseAll();
            return n == 0 ? "No windows were under agent control." : $"Released {n} window(s); original states restored.";
        }
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Provide 'query', or set all=true to release everything.");

        var match = ResolveWindowOrThrow(query);
        bool ok = WindowControl.Release(match.Hwnd);
        return ok
            ? $"Released '{match.Title}' [{match.ProcessName}.exe, PID {match.Pid}]; frame removed, top-most state restored."
            : $"Window '{match.Title}' was not under agent control (nothing to release).";
    }

    [McpServerTool(Name = "record_window")]
    [Description(
        "Screen-records ONE window to an MP4 (H.264) file for a fixed duration, then returns the file path.\n" +
        "Frames are captured with the same DWM-aware method as screenshot_window, so occluded / Chromium / " +
        "UWP / WebView2 surfaces record correctly. The 'Under Agent Control' frame is NOT recorded (only the " +
        "window's own pixels are captured).\n" +
        "REQUIREMENTS:\n" +
        "  • The window must be under agent control first (call control_window). This keeps it top-most and " +
        "shows the user you are recording it.\n" +
        "  • FFmpeg must be available (on PATH, or via the TOTALCONTROL_FFMPEG env var). Install: " +
        "'winget install Gyan.FFmpeg'.\n" +
        "ABORTABLE: if the user clicks ✕ on the control frame during recording, the capture stops early and " +
        "the partial MP4 is still finalized.\n" +
        "NOTE: this call BLOCKS for approximately 'durationSeconds'. Choose a bounded duration.")]
    public static string RecordWindow(
        [Description("Window title or process name to record. Must already be under agent control.")]
        string query,
        [Description("Recording length in seconds. Range 1..300.")]
        int durationSeconds = 10,
        [Description("Frames per second. Range 1..60. Default 15 (good balance of smoothness and file size).")]
        int fps = 15,
        [Description("Optional output .mp4 path. Default: %USERPROFILE%\\Videos\\TotalControl\\<title>_<timestamp>.mp4.")]
        string? outputPath = null)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.");
        if (durationSeconds < 1 || durationSeconds > 300) throw new ArgumentOutOfRangeException(nameof(durationSeconds), "durationSeconds must be 1..300.");
        if (fps < 1 || fps > 60) throw new ArgumentOutOfRangeException(nameof(fps), "fps must be 1..60.");

        var match = ResolveWindowOrThrow(query);

        var gate = Enforcement.GateTarget(WindowControl.IsControlled(match.Hwnd), match.Title, "record_window");
        if (gate is not null) return gate;

        // Resolve output path.
        string outPath;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            outPath = outputPath;
            if (!outPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) outPath += ".mp4";
        }
        else
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "TotalControl");
            var safe = string.Concat(match.Title.Split(Path.GetInvalidFileNameChars())).Trim();
            if (safe.Length == 0) safe = "window";
            if (safe.Length > 60) safe = safe[..60];
            outPath = Path.Combine(dir, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        }

        using var cts = new CancellationTokenSource();
        using (AgentSession.RegisterOperation(cts))
        {
            return ScreenRecorder.Record(match.Hwnd, match.Title, durationSeconds, fps, outPath, cts.Token);
        }
    }

    // ============================================================
    //  HIGH-LEVEL / UI AUTOMATION
    // ============================================================

    [McpServerTool(Name = "click_in_window")]
    [Description(
        "Clicks at a coordinate expressed RELATIVE TO A SPECIFIC WINDOW'S top-left corner. The server " +
        "resolves the window, reads its true DWM frame bounds, adds (imageX, imageY) to the frame origin " +
        "and synthesizes the click — you never have to compute screen pixels yourself.\n" +
        "WHY THIS EXISTS: the #1 source of failed automation is mis-computing screen coordinates from a " +
        "downscaled screenshot rendered in chat. 'screenshot_window' returns a PNG whose (0,0) IS the " +
        "window's top-left in physical pixels. Pass those same image coordinates straight to this tool " +
        "and the click is guaranteed to land where you intended.\n" +
        "Canonical loop:\n" +
        "  1. screenshot_window query='Calculator'   → image is 320×480 at screen (1200, 300)\n" +
        "  2. Locate the '7' key at image-coord (40, 220) by inspecting the PNG.\n" +
        "  3. click_in_window query='Calculator' imageX=40 imageY=220   → lands at screen (1240, 520).\n" +
        "Window lookup uses the same four-pass match as 'screenshot_window' (exact title → exact process " +
        "→ substring title → substring process). 'activate=true' (the default) raises the window before " +
        "clicking so the click reaches the intended target rather than whatever is on top.\n" +
        "Returns a confirmation string with both the image coords and the resolved screen coords for audit.")]
    public static string ClickInWindow(
        [Description("Window title or process name to find. Tried in order: exact title → exact process → substring title → substring process. Examples: 'Calculator', 'Notepad', 'Visual Studio Code'.")]
        string query,
        [Description("X coordinate INSIDE the window, in physical pixels from the window's top-left. 0 = leftmost column of the window's visible frame. Must equal an X coordinate in a 'screenshot_window' PNG of the same window.")]
        int imageX,
        [Description("Y coordinate INSIDE the window, in physical pixels from the window's top-left. 0 = top row of the window's visible frame.")]
        int imageY,
        [Description("Mouse button to click. Allowed: 'left', 'right', 'middle'. Default 'left'.")]
        string button = "left",
        [Description("Number of consecutive clicks. 1 = single (default). 2 = double-click. Range 1..5.")]
        int count = 1,
        [Description("If true (default), bring the window to the foreground before clicking — strongly recommended so the click actually reaches THIS window and not whatever else is on top.")]
        bool activate = true)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.");

        var match = ResolveWindowOrThrow(query);

        var gate = Enforcement.GateTarget(WindowControl.IsControlled(match.Hwnd), match.Title, "click_in_window");
        if (gate is not null) return gate;

        // Same DWM-first rect logic as screenshot_window so the coordinate
        // spaces are guaranteed to align.
        if (Win32.DwmGetWindowAttribute(match.Hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<Win32.RECT>()) != 0
            || rect.Width <= 0 || rect.Height <= 0)
        {
            Win32.GetWindowRect(match.Hwnd, out rect);
        }

        if (imageX < 0 || imageX >= rect.Width || imageY < 0 || imageY >= rect.Height)
            throw new ArgumentOutOfRangeException(
                nameof(imageX),
                $"({imageX},{imageY}) is outside the window's frame ({rect.Width}×{rect.Height}).");

        int sx = rect.Left + imageX;
        int sy = rect.Top  + imageY;

        if (Win32.IsIconic(match.Hwnd)) Win32.ShowWindow(match.Hwnd, Win32.SW_RESTORE);
        if (activate) Win32.SetForegroundWindow(match.Hwnd);

        // Delegate to ClickMouse so the SendInput logic stays single-sourced.
        var inner = ClickMouse(button, sx, sy, count);
        return $"{inner}  ←  window '{match.Title}' [{match.ProcessName}.exe, PID {match.Pid}] origin ({rect.Left},{rect.Top}) + image ({imageX},{imageY}).";
    }

    [McpServerTool(Name = "hover_preview")]
    [Description(
        "Moves the cursor to (x, y) and returns a small PNG crop of the screen centered there, with a " +
        "high-contrast crosshair drawn at the cursor's actual landing position. Use this to VERIFY a " +
        "coordinate visually BEFORE committing to 'click_mouse' on a destructive control.\n" +
        "The cursor is NOT captured by the OS into screenshots normally — this tool composites a synthetic " +
        "crosshair on top of the captured pixels so you can see exactly where the click will land.\n" +
        "Typical defensive pattern:\n" +
        "  1. hover_preview x=… y=…           ← look at the crop; is the crosshair on the intended target?\n" +
        "  2. If yes → click_mouse (cursor is already there — omit x/y for a click-in-place).\n" +
        "  3. If no  → adjust coords and hover again.\n" +
        "Returns the cursor's actual position plus the cropped image.")]
    public static IList<ContentBlock> HoverPreview(
        [Description("Target X coordinate in physical pixels (same coordinate space as 'move_mouse').")]
        int x,
        [Description("Target Y coordinate in physical pixels.")]
        int y,
        [Description("Half-width / half-height of the square crop, in pixels. Default 80 (so the preview is ~160×160). Range 16..500.")]
        int radius = 80)
    {
        if (radius < 16 || radius > 500) throw new ArgumentOutOfRangeException(nameof(radius), "radius must be 16..500.");

        if (!Win32.SetCursorPos(x, y))
            throw new InvalidOperationException($"SetCursorPos({x},{y}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        Win32.GetCursorPos(out var p);

        // Clip the crop to the virtual screen so we never BitBlt past the edges.
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vr = vx + Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vb = vy + Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        int left   = Math.Max(vx, x - radius);
        int top    = Math.Max(vy, y - radius);
        int right  = Math.Min(vr, x + radius);
        int bottom = Math.Min(vb, y + radius);
        int w = Math.Max(1, right - left);
        int h = Math.Max(1, bottom - top);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(new Point(left, top), Point.Empty, new Size(w, h), CopyPixelOperation.SourceCopy);

            // Crosshair: double-stroked (3px black under 1px yellow) so it stays
            // visible on any background. Two short arms with a 3px gap at the
            // origin reveals the underlying pixel instead of covering it.
            int cx = p.X - left, cy = p.Y - top;
            using var penBlack  = new Pen(Color.Black, 3);
            using var penYellow = new Pen(Color.Yellow, 1);
            g.DrawLine(penBlack, cx - 16, cy,      cx - 4,  cy);
            g.DrawLine(penBlack, cx + 4,  cy,      cx + 16, cy);
            g.DrawLine(penBlack, cx,      cy - 16, cx,      cy - 4);
            g.DrawLine(penBlack, cx,      cy + 4,  cx,      cy + 16);
            g.DrawLine(penYellow, cx - 15, cy,      cx - 5,  cy);
            g.DrawLine(penYellow, cx + 5,  cy,      cx + 15, cy);
            g.DrawLine(penYellow, cx,      cy - 15, cx,      cy - 5);
            g.DrawLine(penYellow, cx,      cy + 5,  cx,      cy + 15);
            g.DrawRectangle(penBlack, cx - 1, cy - 1, 2, 2);
        }

        var meta = $"Cursor moved to ({p.X},{p.Y}). Preview {w}×{h} of screen region ({left},{top})-({right},{bottom}). " +
                   $"Crosshair marks the cursor at preview-relative ({p.X - left},{p.Y - top}).";
        return BuildImageResult(bmp, meta);
    }

    [McpServerTool(Name = "find_element")]
    [Description(
        "Walks the UI Automation accessibility tree of a window and returns the matching controls with " +
        "their CENTER coordinates in physical pixels. Optionally invokes the first match through the " +
        "accessibility layer (UIA Invoke / Toggle / SelectionItem / ExpandCollapse pattern), which " +
        "reaches buttons that synthetic SendInput clicks sometimes cannot — notably UWP apps, " +
        "AppContainer-isolated processes, and apps running at a higher integrity level.\n" +
        "WHY USE THIS over screenshot+click_mouse: zero pixel guesswork. UIA tells you EXACTLY where a " +
        "named control lives, even when its position shifts due to layout, theming, or DPI scaling.\n" +
        "Filters (combine freely; all must match):\n" +
        "  • name           — the user-visible label, e.g. 'OK', 'File', 'Klondike'. Case-insensitive.\n" +
        "  • automationId   — the developer-assigned id, e.g. 'okButton'. Case-sensitive (per UIA).\n" +
        "  • controlType    — one of: button, edit, text, statictext, image, list, listitem, menu, " +
        "menuitem, tab, tabitem, tree, treeitem, hyperlink, checkbox, radiobutton, combobox, group, " +
        "pane, window, splitbutton, statusbar, toolbar, scrollbar, slider, spinner, calendar, datagrid, " +
        "datagriditem, header, headeritem, separator, thumb, titlebar, tooltip, custom.\n" +
        "If 'invoke=true' the FIRST matching element is activated via the best available pattern " +
        "(Invoke for buttons / menu items; Toggle for checkboxes; SelectionItem for list / tab items; " +
        "ExpandCollapse for combo boxes / tree nodes). If no pattern is supported, the cursor is moved " +
        "to the element's center and a real click is synthesized as a fallback.\n" +
        "Example: invoke the Solitaire 'Klondike' tile without computing pixels:\n" +
        "  find_element query='Solitaire' name='Klondike' invoke=true")]
    public static string FindElement(
        [Description("Window title or process name to search inside. Same lookup rules as 'screenshot_window'.")]
        string query,
        [Description("Optional: filter by user-visible Name (case-insensitive substring or exact). Pass null/empty to skip.")]
        string? name = null,
        [Description("Optional: filter by developer-assigned AutomationId (case-sensitive exact). Pass null/empty to skip.")]
        string? automationId = null,
        [Description("Optional: filter by control type (e.g. 'button', 'menuitem'). See description for full list. Pass null/empty to skip.")]
        string? controlType = null,
        [Description("If true, activate the FIRST match via UIA (Invoke / Toggle / SelectionItem / ExpandCollapse) or fall back to a real click on its center. Default false (find-only).")]
        bool invoke = false,
        [Description("Maximum number of matches to list in the result. Default 10. Range 1..100.")]
        int maxResults = 10,
        [Description("If true (default), bring the window to the foreground before searching/invoking so the action targets the right window.")]
        bool activate = true)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.");
        if (maxResults < 1 || maxResults > 100) throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be 1..100.");

        var match = ResolveWindowOrThrow(query);

        var gate = Enforcement.GateTarget(WindowControl.IsControlled(match.Hwnd), match.Title, "find_element");
        if (gate is not null) return gate;

        if (Win32.IsIconic(match.Hwnd)) Win32.ShowWindow(match.Hwnd, Win32.SW_RESTORE);
        if (activate) Win32.SetForegroundWindow(match.Hwnd);

        AutomationElement? root;
        try { root = AutomationElement.FromHandle(match.Hwnd); }
        catch (Exception ex) { throw new InvalidOperationException($"AutomationElement.FromHandle failed for '{match.Title}': {ex.Message}", ex); }
        if (root is null) throw new InvalidOperationException($"No UIA root for window '{match.Title}'.");

        // Build the search condition. If no filter was supplied, return EVERY
        // element under the window root capped at maxResults — useful for a
        // first-pass "what's in this window?" survey.
        var conds = new List<Condition>();
        if (!string.IsNullOrEmpty(name))
            conds.Add(new PropertyCondition(AutomationElement.NameProperty, name));
        if (!string.IsNullOrEmpty(automationId))
            conds.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (!string.IsNullOrEmpty(controlType))
            conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ResolveControlType(controlType!)));

        Condition cond = conds.Count switch
        {
            0 => Condition.TrueCondition,
            1 => conds[0],
            _ => new AndCondition(conds.ToArray()),
        };

        AutomationElementCollection found;
        try { found = root.FindAll(TreeScope.Descendants | TreeScope.Element, cond); }
        catch (Exception ex) { throw new InvalidOperationException($"UIA FindAll failed: {ex.Message}", ex); }

        // Substring fallback for name: PropertyCondition is exact-match only,
        // but agents often pass partial labels. If exact-match returned nothing
        // AND a name filter was supplied, walk the tree and substring-match.
        if (found.Count == 0 && !string.IsNullOrEmpty(name))
        {
            var partial = new List<AutomationElement>();
            var ctCond = string.IsNullOrEmpty(controlType)
                ? (Condition)Condition.TrueCondition
                : new PropertyCondition(AutomationElement.ControlTypeProperty, ResolveControlType(controlType!));
            var all = root.FindAll(TreeScope.Descendants | TreeScope.Element, ctCond);
            foreach (AutomationElement el in all)
            {
                try
                {
                    var nm = el.Current.Name ?? "";
                    if (nm.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(automationId) && el.Current.AutomationId != automationId) continue;
                        partial.Add(el);
                        if (partial.Count >= maxResults * 2) break;
                    }
                }
                catch { /* element may have disappeared mid-walk; skip */ }
            }
            if (partial.Count > 0)
            {
                return FormatFound(partial, match, name, automationId, controlType, invoke, maxResults, partial: true);
            }
        }

        var list = new List<AutomationElement>();
        foreach (AutomationElement el in found)
        {
            list.Add(el);
            if (list.Count >= maxResults * 2) break;
        }
        return FormatFound(list, match, name, automationId, controlType, invoke, maxResults, partial: false);
    }

    /// <summary>
    /// Render the matched UIA elements as a text report and, when invoke=true,
    /// activate the first one through the most appropriate pattern. Kept as a
    /// helper so the exact-match and substring-match paths in FindElement share
    /// the same output format.
    /// </summary>
    private static string FormatFound(
        IList<AutomationElement> matches,
        WindowInfo match,
        string? name, string? automationId, string? controlType,
        bool invoke, int maxResults, bool partial)
    {
        var sb = new StringBuilder();
        sb.Append($"find_element in '{match.Title}' [{match.ProcessName}.exe, PID {match.Pid}] — ")
          .Append(matches.Count).Append(" match")
          .Append(matches.Count == 1 ? "" : "es");
        if (partial) sb.Append(" (substring fallback)");
        sb.AppendLine(":");

        if (matches.Count == 0)
        {
            sb.AppendLine("  (no elements matched — try a different name/controlType, or call without filters to dump the tree)");
            return sb.ToString();
        }

        int n = Math.Min(matches.Count, maxResults);
        for (int i = 0; i < n; i++)
        {
            var el = matches[i];
            try
            {
                var c = el.Current;
                var rect = c.BoundingRectangle;
                int cx = (int)Math.Round(rect.X + rect.Width  / 2.0);
                int cy = (int)Math.Round(rect.Y + rect.Height / 2.0);
                var ct = c.ControlType?.LocalizedControlType ?? "?";
                var nm = string.IsNullOrEmpty(c.Name) ? "" : c.Name;
                var aid = string.IsNullOrEmpty(c.AutomationId) ? "" : c.AutomationId;
                bool enabled = c.IsEnabled, offscreen = c.IsOffscreen;
                sb.Append($"  [{i}] {ct} name='{nm}' id='{aid}' center=({cx},{cy}) bounds=({(int)rect.X},{(int)rect.Y},{(int)rect.Width}×{(int)rect.Height})");
                if (!enabled)  sb.Append(" DISABLED");
                if (offscreen) sb.Append(" OFFSCREEN");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  [{i}] (element vanished during read: {ex.Message})");
            }
        }
        if (matches.Count > n)
            sb.AppendLine($"  ... and {matches.Count - n} more (raise maxResults to see).");

        if (invoke)
        {
            var first = matches[0];
            try
            {
                var rect = first.Current.BoundingRectangle;
                int cx = (int)Math.Round(rect.X + rect.Width  / 2.0);
                int cy = (int)Math.Round(rect.Y + rect.Height / 2.0);
                var action = InvokeElement(first, cx, cy);
                sb.Append("  → invoked [0] via ").Append(action).Append(" at (").Append(cx).Append(',').Append(cy).AppendLine(").");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  → INVOKE FAILED: {ex.Message}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Activate a UIA element through the best available pattern, falling back
    /// to a real mouse click if nothing else applies. Returns a short label of
    /// what was used so the caller can include it in the result message.
    /// </summary>
    private static string InvokeElement(AutomationElement el, int centerX, int centerY)
    {
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p) && p is InvokePattern inv)
        { inv.Invoke(); return "InvokePattern"; }
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out p) && p is TogglePattern tog)
        { tog.Toggle(); return "TogglePattern"; }
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p) && p is SelectionItemPattern sel)
        { sel.Select(); return "SelectionItemPattern"; }
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p) && p is ExpandCollapsePattern ec)
        {
            ec.Expand();
            return "ExpandCollapsePattern";
        }
        // Last resort: real synthesized click at the element's center.
        Win32.SetCursorPos(centerX, centerY);
        SendInputs(new[]
        {
            MouseEvent(Win32.MouseEventF.LeftDown),
            MouseEvent(Win32.MouseEventF.LeftUp),
        });
        return "synthetic LeftClick (no UIA pattern available)";
    }

    /// <summary>
    /// Map a free-text control-type name to the corresponding UIA
    /// <see cref="ControlType"/>. Accepts both the canonical name ('Button')
    /// and a few common aliases ('textbox' → Edit). Throws on unknown names so
    /// the agent gets a clear error instead of a silent empty result.
    /// </summary>
    private static ControlType ResolveControlType(string s) => s.Trim().ToLowerInvariant() switch
    {
        "button"         => ControlType.Button,
        "calendar"       => ControlType.Calendar,
        "checkbox"       => ControlType.CheckBox,
        "combobox" or "combo" => ControlType.ComboBox,
        "custom"         => ControlType.Custom,
        "datagrid"       => ControlType.DataGrid,
        "datagriditem" or "dataitem" => ControlType.DataItem,
        "document"       => ControlType.Document,
        "edit" or "textbox" or "textfield" => ControlType.Edit,
        "group"          => ControlType.Group,
        "header"         => ControlType.Header,
        "headeritem"     => ControlType.HeaderItem,
        "hyperlink" or "link" => ControlType.Hyperlink,
        "image"          => ControlType.Image,
        "list"           => ControlType.List,
        "listitem"       => ControlType.ListItem,
        "menu"           => ControlType.Menu,
        "menubar"        => ControlType.MenuBar,
        "menuitem"       => ControlType.MenuItem,
        "pane"           => ControlType.Pane,
        "progressbar"    => ControlType.ProgressBar,
        "radiobutton" or "radio" => ControlType.RadioButton,
        "scrollbar"      => ControlType.ScrollBar,
        "separator"      => ControlType.Separator,
        "slider"         => ControlType.Slider,
        "spinner"        => ControlType.Spinner,
        "splitbutton"    => ControlType.SplitButton,
        "statusbar"      => ControlType.StatusBar,
        "tab"            => ControlType.Tab,
        "tabitem"        => ControlType.TabItem,
        "text" or "statictext" or "label" => ControlType.Text,
        "thumb"          => ControlType.Thumb,
        "titlebar"       => ControlType.TitleBar,
        "toolbar"        => ControlType.ToolBar,
        "tooltip"        => ControlType.ToolTip,
        "tree"           => ControlType.Tree,
        "treeitem"       => ControlType.TreeItem,
        "window"         => ControlType.Window,
        _ => throw new ArgumentException($"Unknown controlType '{s}'. See the find_element tool description for the supported list."),
    };

    /// <summary>
    /// Resolve a free-text query to a single visible top-level window or
    /// throw with a candidate list. Shared by click_in_window and find_element
    /// so both tools fail with the same actionable error.
    /// </summary>
    private static WindowInfo ResolveWindowOrThrow(string query)
    {
        var windows = EnumerateTopLevelWindows();
        var match = FindWindow(windows, query);
        if (match is not null) return match;

        var sb = new StringBuilder();
        sb.AppendLine($"No window matched '{query}'.");
        var candidates = windows
            .Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || w.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(15).ToList();
        if (candidates.Count > 0)
        {
            sb.AppendLine("Candidates:");
            foreach (var c in candidates) sb.AppendLine($"  • '{c.Title}' [{c.ProcessName}.exe, PID {c.Pid}]");
        }
        else
        {
            sb.AppendLine("Top visible windows:");
            foreach (var w in windows.Take(15)) sb.AppendLine($"  • '{w.Title}' [{w.ProcessName}.exe, PID {w.Pid}]");
        }
        throw new ArgumentException(sb.ToString());
    }

    // ============================================================
    //  Helpers
    // ============================================================

    /// <summary>Builds a single mouse INPUT event with no movement and the supplied flag.</summary>
    private static Win32.INPUT MouseEvent(Win32.MouseEventF flag) => new()
    {
        type = Win32.INPUT_MOUSE,
        U = new Win32.INPUTUNION { mi = new Win32.MOUSEINPUT { dwFlags = (uint)flag } }
    };

    /// <summary>
    /// Builds a MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK event that lands on
    /// the supplied physical-pixel coordinate. The OS expects the coordinate
    /// in a normalized 0..65535 grid spanning the entire virtual screen, so we
    /// project (x, y) onto that grid using the current SM_*VIRTUALSCREEN metrics.
    /// Sending this (rather than a bare SetCursorPos) is what makes drag gestures
    /// produce real WM_MOUSEMOVE events that DragDetect-based apps recognize.
    /// </summary>
    private static Win32.INPUT AbsoluteMoveEvent(int x, int y)
    {
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
        return AbsoluteMoveEventRaw(x, y, vx, vy, vw, vh);
    }

    /// <summary>
    /// Like <see cref="AbsoluteMoveEvent"/> but takes pre-computed virtual-screen bounds so the
    /// caller (e.g. drag_mouse) can cache the metrics once instead of re-reading them per step.
    /// </summary>
    private static Win32.INPUT AbsoluteMoveEventRaw(int x, int y, int vx, int vy, int vw, int vh)
    {
        int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        return new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.INPUTUNION
            {
                mi = new Win32.MOUSEINPUT
                {
                    dx = ax,
                    dy = ay,
                    dwFlags = (uint)(Win32.MouseEventF.Move | Win32.MouseEventF.Absolute | Win32.MouseEventF.VirtualDesk),
                }
            }
        };
    }

    /// <summary>
    /// Push a batch of INPUT events through SendInput. Throws if the OS
    /// accepted fewer than we sent (typically a UIPI block from an elevated
    /// foreground window — TotalControl runs un-elevated by default).
    /// </summary>
    private static void SendInputs(IList<Win32.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs as Win32.INPUT[] ?? inputs.ToArray();
        var sent = Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
        if (sent != arr.Length)
            throw new InvalidOperationException($"SendInput accepted {sent}/{arr.Length} events (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Compact projection of a top-level window.</summary>
    private sealed record WindowInfo(IntPtr Hwnd, string Title, uint Pid, string ProcessName);

    /// <summary>
    /// Walk every visible top-level window and capture (hwnd, title, pid,
    /// process name). Invisible / cloaked / titleless windows are skipped to
    /// keep the candidate list focused on things the user actually sees.
    /// Process names are resolved through <see cref="GetProcessName"/> which
    /// caches results for 10 s to avoid repeated kernel calls on hot paths.
    /// </summary>
    private static List<WindowInfo> EnumerateTopLevelWindows()
    {
        var list = new List<WindowInfo>();
        Win32.EnumWindows((hWnd, _) =>
        {
            if (!Win32.IsWindowVisible(hWnd)) return true;
            int len = Win32.GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            Win32.GetWindowText(hWnd, sb, sb.Capacity);
            Win32.GetWindowThreadProcessId(hWnd, out var pid);
            list.Add(new WindowInfo(hWnd, sb.ToString(), pid, GetProcessName(pid)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Resolve a free-text query to a single window using a four-pass match:
    /// exact title → exact process → substring title → substring process.
    /// Returns null when no candidate matches (caller falls back to listing
    /// candidates so the agent can refine its query).
    /// </summary>
    private static WindowInfo? FindWindow(List<WindowInfo> windows, string query)
    {
        var byTitleExact = windows.FirstOrDefault(w => string.Equals(w.Title, query, StringComparison.OrdinalIgnoreCase));
        if (byTitleExact is not null) return byTitleExact;

        // Accept "notepad" and "notepad.exe" interchangeably for process matching.
        var procQuery = query.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? query[..^4] : query;
        var byProcExact = windows.FirstOrDefault(w => string.Equals(w.ProcessName, procQuery, StringComparison.OrdinalIgnoreCase));
        if (byProcExact is not null) return byProcExact;

        var byTitlePartial = windows.FirstOrDefault(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (byTitlePartial is not null) return byTitlePartial;

        var byProcPartial = windows.FirstOrDefault(w => w.ProcessName.Contains(procQuery, StringComparison.OrdinalIgnoreCase));
        return byProcPartial;
    }

    /// <summary>
    /// Encode a bitmap and wrap it into the MCP content-block pair the agent expects.
    /// <paramref name="scale"/> downsamples before encoding (0.1–1.0; 1.0 = original size).
    /// <paramref name="jpegQuality"/> selects JPEG encoding at 0–100 quality (-1 = lossless PNG).
    /// </summary>
    private static IList<ContentBlock> BuildImageResult(
        Bitmap bmp, string metadata,
        float scale = 1f, int jpegQuality = -1)
    {
        // Downsample if requested.
        Bitmap workBmp = bmp;
        bool ownWork = false;
        if (scale is > 0f and < 1f)
        {
            int sw = Math.Max(1, (int)(bmp.Width  * scale));
            int sh = Math.Max(1, (int)(bmp.Height * scale));
            workBmp = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            ownWork = true;
            using var g = Graphics.FromImage(workBmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, sw, sh);
        }

        // Capture output dimensions NOW, before workBmp may be disposed below —
        // reading them after Dispose() throws "Parameter is not valid".
        int outW = workBmp.Width, outH = workBmp.Height;
        int srcW = bmp.Width, srcH = bmp.Height;

        string mimeType;
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            if (jpegQuality >= 0)
            {
                mimeType = "image/jpeg";
                var codec  = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == mimeType);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 0, 100));
                workBmp.Save(ms, codec, ep);
            }
            else
            {
                mimeType = "image/png";
                workBmp.Save(ms, ImageFormat.Png);
            }
            bytes = ms.ToArray();
        }
        finally
        {
            if (ownWork) workBmp.Dispose();
        }

        string fmt  = jpegQuality >= 0 ? $"JPEG q{jpegQuality}" : "PNG";
        string dims = scale is > 0f and < 1f
            ? $"{outW}×{outH} (scaled {scale:P0} from {srcW}×{srcH})"
            : $"{srcW}×{srcH}";

        return new List<ContentBlock>
        {
            new TextContentBlock { Text = metadata + $" {fmt} {dims}, {bytes.Length / 1024} KB." },
            ImageContentBlock.FromBytes(bytes, mimeType),
        };
    }
}
