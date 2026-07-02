// -----------------------------------------------------------------------------
//  TotalControl — Window control + "Under Agent Control" overlay frames
//
//  Licensed under the MIT License. See LICENSE in the project root for details.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Provides the machinery behind the control_window / release_window tools:
//    • Makes a target window top-most (recording its prior state so it can be
//      restored exactly) and brings it forward.
//    • Draws a crimson border around it and a "Under Agent Control" tag with an
//      ✕ button above its titlebar, so the user can SEE which window the agent
//      is driving and reclaim it at any time by clicking ✕.
//    • Follows the target as it moves / resizes, hides while it is minimized,
//      and restores everything (border removed, top-most reverted) on release
//      or on process exit.
//
//  The overlays are WPF windows, so they run on a dedicated STA thread with its
//  own Dispatcher and a follow-timer. All public methods marshal onto that
//  thread. Overlay windows carry no title text and the WS_EX_TOOLWINDOW style,
//  so they never appear in the agent's own window enumeration.
// -----------------------------------------------------------------------------

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TotalControl;

internal static class WindowControl
{
    /// <summary>Bookkeeping for one window currently under agent control.</summary>
    private sealed class Controlled
    {
        public required IntPtr Target;
        public required string Title;
        public bool WasTopmost;
        public OverlayBorder Border = null!;
        public OverlayTag Tag = null!;
        public bool OverlaysVisible = true;
        public Win32.RECT LastRect;
        public bool HasLastRect;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, Controlled> Controlled_ = new();
    private static Thread? UiThread;
    private static Dispatcher? UiDispatcher;
    private static DispatcherTimer? FollowTimer;
    private static bool ExitHookInstalled;

    // -------------------------------------------------------------------------
    //  Public API (thread-safe; marshals onto the UI dispatcher)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Put a window under agent control: record its top-most state, force it
    /// top-most, bring it forward, and show the crimson frame + tag. Idempotent —
    /// re-acquiring an already-controlled window just re-asserts foreground.
    /// </summary>
    public static string Acquire(IntPtr hwnd, string title)
    {
        EnsureUiThread();
        return UiDispatcher!.Invoke(() =>
        {
            lock (Gate)
            {
                if (Controlled_.TryGetValue(hwnd, out var existing))
                {
                    Win32.SetForegroundWindow(hwnd);
                    return $"Window '{existing.Title}' is already under agent control.";
                }

                int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
                bool wasTopmost = (ex & Win32.WS_EX_TOPMOST) != 0;

                var c = new Controlled { Target = hwnd, Title = title, WasTopmost = wasTopmost };

                // Force top-most and bring forward (best-effort foreground).
                Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                Win32.SetForegroundWindow(hwnd);

                c.Border = new OverlayBorder();
                c.Tag = new OverlayTag(title);
                // User clicking ✕ is an explicit ABORT: cancel in-flight operations and
                // arm the one-shot abort flag, THEN release the window.
                c.Tag.CloseRequested += () =>
                {
                    AgentSession.SignalUserAbort(title);
                    Release(hwnd);
                };
                c.Border.Show();
                c.Tag.Show();

                Controlled_[hwnd] = c;
                PositionOverlays(c, force: true);

                FollowTimer!.Start();
                return $"Window '{title}' is now UNDER AGENT CONTROL (top-most, crimson frame shown).";
            }
        });
    }

    /// <summary>Release one controlled window: remove its overlays and restore its prior top-most state.</summary>
    public static bool Release(IntPtr hwnd)
    {
        if (UiDispatcher is null) return false;
        return UiDispatcher.Invoke(() => ReleaseOnUi(hwnd, restore: true));
    }

    /// <summary>Release every controlled window. Returns how many were released.</summary>
    public static int ReleaseAll()
    {
        if (UiDispatcher is null) return 0;
        return UiDispatcher.Invoke(() =>
        {
            lock (Gate)
            {
                var handles = Controlled_.Keys.ToList();
                foreach (var h in handles) ReleaseOnUi(h, restore: true);
                return handles.Count;
            }
        });
    }

    /// <summary>Titles of all windows currently under agent control (for status reporting).</summary>
    public static IReadOnlyList<string> ListControlled()
    {
        lock (Gate) return Controlled_.Values.Select(c => c.Title).ToList();
    }

    /// <summary>True when at least one window is currently under agent control.</summary>
    public static bool AnyControlled()
    {
        lock (Gate) return Controlled_.Count > 0;
    }

    /// <summary>True when the specified window handle is currently under agent control.</summary>
    public static bool IsControlled(IntPtr hwnd)
    {
        lock (Gate) return Controlled_.ContainsKey(hwnd);
    }

    // -------------------------------------------------------------------------
    //  UI-thread internals
    // -------------------------------------------------------------------------

    private static bool ReleaseOnUi(IntPtr hwnd, bool restore)
    {
        lock (Gate)
        {
            if (!Controlled_.TryGetValue(hwnd, out var c)) return false;

            try { c.Border.Close(); } catch { /* already gone */ }
            try { c.Tag.Close(); } catch { /* already gone */ }

            // Revert top-most only if the window was NOT top-most before we grabbed it.
            if (restore && !c.WasTopmost && Win32.IsWindow(hwnd))
            {
                Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }

            Controlled_.Remove(hwnd);
            if (Controlled_.Count == 0) FollowTimer?.Stop();
            return true;
        }
    }

    /// <summary>Timer tick: follow each target, hide/show with its minimized state, drop dead windows.</summary>
    private static void Follow(object? sender, EventArgs e)
    {
        lock (Gate)
        {
            List<IntPtr>? dead = null;
            foreach (var c in Controlled_.Values)
            {
                if (!Win32.IsWindow(c.Target)) { (dead ??= new()).Add(c.Target); continue; }

                bool hidden = Win32.IsIconic(c.Target) || !Win32.IsWindowVisible(c.Target);
                if (hidden)
                {
                    if (c.OverlaysVisible)
                    {
                        c.Border.Hide();
                        c.Tag.Hide();
                        c.OverlaysVisible = false;
                    }
                    continue;
                }

                if (!c.OverlaysVisible)
                {
                    c.Border.Show();
                    c.Tag.Show();
                    c.OverlaysVisible = true;
                }

                PositionOverlays(c, force: false);
            }

            if (dead is not null)
                foreach (var h in dead) ReleaseOnUi(h, restore: false); // window gone; nothing left to restore
        }
    }

    /// <summary>
    /// Place the crimson frame around the target and the tag above its titlebar,
    /// in physical pixels, then re-assert top-most z-order so they stay above the
    /// (also top-most) target even after the user clicks it.
    /// </summary>
    private static void PositionOverlays(Controlled c, bool force)
    {
        if (!Win32.GetWindowRect(c.Target, out var r)) return;

        bool moved = force || !c.HasLastRect ||
                     r.Left != c.LastRect.Left || r.Top != c.LastRect.Top ||
                     r.Right != c.LastRect.Right || r.Bottom != c.LastRect.Bottom;
        c.LastRect = r;
        c.HasLastRect = true;

        uint dpi = Win32.GetDpiForWindow(c.Target);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int bt   = (int)Math.Round(3 * scale);    // border thickness (physical px)
        int tagH = (int)Math.Round(26 * scale);
        int tagW = (int)Math.Round(210 * scale);

        var borderH = new WindowInteropHelper(c.Border).Handle;
        var tagHwnd = new WindowInteropHelper(c.Tag).Handle;

        uint zFlags = Win32.SWP_NOACTIVATE;
        uint moveFlags = moved ? zFlags : zFlags | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE;

        // Frame: inflate the target rect by the border thickness on every side.
        Win32.SetWindowPos(borderH, Win32.HWND_TOPMOST,
            r.Left - bt, r.Top - bt, (r.Right - r.Left) + bt * 2, (r.Bottom - r.Top) + bt * 2, moveFlags);

        // Tag: sits just above the titlebar (and above the frame's top edge).
        Win32.SetWindowPos(tagHwnd, Win32.HWND_TOPMOST,
            r.Left, r.Top - tagH - bt, tagW, tagH, moveFlags);
    }

    // -------------------------------------------------------------------------
    //  Dedicated STA UI thread + dispatcher
    // -------------------------------------------------------------------------

    private static void EnsureUiThread()
    {
        lock (Gate)
        {
            if (UiThread is not null) return;

            using var ready = new ManualResetEventSlim(false);
            UiThread = new Thread(() =>
            {
                UiDispatcher = Dispatcher.CurrentDispatcher;
                FollowTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(75),
                };
                FollowTimer.Tick += Follow;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "TotalControl-Overlay-UI",
            };
            UiThread.SetApartmentState(ApartmentState.STA);
            UiThread.Start();
            ready.Wait();

            if (!ExitHookInstalled)
            {
                // Safety net: if the server dies while windows are controlled,
                // un-top-most them so nothing is left stuck above everything.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreAllOnExit();
                ExitHookInstalled = true;
            }
        }
    }

    /// <summary>
    /// Process-exit cleanup. Overlays vanish with the process, so we only need
    /// to revert top-most state. Runs synchronously on whatever thread is
    /// tearing the process down — SetWindowPos is safe to call cross-thread.
    /// </summary>
    private static void RestoreAllOnExit()
    {
        lock (Gate)
        {
            foreach (var c in Controlled_.Values)
            {
                if (!c.WasTopmost && Win32.IsWindow(c.Target))
                {
                    Win32.SetWindowPos(c.Target, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                }
            }
            Controlled_.Clear();
        }
    }

    // -------------------------------------------------------------------------
    //  Overlay windows (WPF)
    // -------------------------------------------------------------------------

    /// <summary>Click-through crimson frame drawn around the controlled window.</summary>
    private sealed class OverlayBorder : Window
    {
        public OverlayBorder()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            ShowActivated = false;

            Content = new Border
            {
                BorderBrush = Brushes.Crimson,
                BorderThickness = new Thickness(3),
                Background = Brushes.Transparent, // hollow centre → target stays visible
            };

            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
                ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT
                    | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
                Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, ex);
            };
        }
    }

    /// <summary>Interactive "Under Agent Control" tag with an ✕ button.</summary>
    private sealed class OverlayTag : Window
    {
        public event Action? CloseRequested;

        public OverlayTag(string title)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;

            var label = new TextBlock
            {
                Text = "Under Agent Control",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 6, 0),
                ToolTip = title,
            };

            var close = new Button
            {
                Content = "✕",
                Foreground = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Release this window (restore normal state)",
            };
            close.Click += (_, _) => CloseRequested?.Invoke();

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(label);
            panel.Children.Add(close);

            Content = new Border
            {
                Background = Brushes.Crimson,
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Child = panel,
            };

            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                int ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
                ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
                Win32.SetWindowLong(h, Win32.GWL_EXSTYLE, ex);
            };
        }
    }
}

/// <summary>
/// Central enforcement for the "you must select a window before you can capture or
/// interact with it" policy. Capture and interaction tools consult this gate; when it
/// blocks, they return the instruction string instead of performing the action.
///
/// Enforcement is ON by default and can be disabled by setting the environment variable
/// TOTALCONTROL_REQUIRE_WINDOW_SELECTION to 0 / false / off / no.
/// </summary>
internal static class Enforcement
{
    /// <summary>Whether the selection gate is active. Reads the env var live so it can be toggled without a rebuild.</summary>
    public static bool RequireSelection
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("TOTALCONTROL_REQUIRE_WINDOW_SELECTION");
            if (string.IsNullOrWhiteSpace(v)) return true; // default ON
            return v.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no" or "disabled");
        }
    }

    private static string ControlledList()
    {
        var titles = WindowControl.ListControlled();
        return titles.Count == 0 ? "(none)" : string.Join(", ", titles.Select(t => $"'{t}'"));
    }

    /// <summary>Surface a pending user-abort (consume-once), independent of the selection gate.</summary>
    public static string? CheckAbort() => AgentSession.ConsumeAbort();

    /// <summary>
    /// Gate a tool that needs SOME window under control (absolute-coordinate tools,
    /// full-screen capture, keyboard). Returns null if allowed, or an instruction
    /// string the caller should return verbatim if blocked.
    /// </summary>
    public static string? GateAny(string tool)
    {
        var abort = AgentSession.ConsumeAbort();
        if (abort is not null) return abort;
        if (!RequireSelection) return null;
        if (WindowControl.AnyControlled()) return null;
        return
            $"⛔ ACTION BLOCKED — no window is under agent control, so '{tool}' will not run.\n" +
            "You MUST select a window first. Do this:\n" +
            "  1. find_window(filter?)   → lists open windows as TEXT (no screenshot needed).\n" +
            "  2. control_window(query)  → selects + pins the target window (a crimson\n" +
            "                              'Under Agent Control' frame appears on it).\n" +
            $"Then retry '{tool}'. This safeguard stops the agent from acting on the wrong or\n" +
            "background window. When finished, call release_window(query) or release_window(all=true).";
    }

    /// <summary>
    /// Gate a tool that targets a SPECIFIC window (per-window capture / inspection /
    /// window-relative click). Returns null if allowed, or an instruction string if the
    /// target window has not been selected via control_window.
    /// </summary>
    public static string? GateTarget(bool isControlled, string title, string tool)
    {
        var abort = AgentSession.ConsumeAbort();
        if (abort is not null) return abort;
        if (!RequireSelection) return null;
        if (isControlled) return null;
        return
            $"⛔ ACTION BLOCKED — the window '{title}' is not under agent control, so '{tool}' will not run.\n" +
            $"'{tool}' can only target a window you have explicitly selected. Call:\n" +
            $"  control_window(query='{title}')\n" +
            $"then retry '{tool}'. This pins the window top-most and frames it so the user can see what\n" +
            $"you are doing. Currently under agent control: {ControlledList()}.";
    }
}
