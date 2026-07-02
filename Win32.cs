// -----------------------------------------------------------------------------
//  TotalControl — Win32 P/Invoke surface
//
//  Licensed under the MIT License. See LICENSE in the project root for details.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using System.Text;

namespace TotalControl;

/// <summary>
/// All P/Invoke declarations TotalControl needs from the Windows API:
///
///   • SendInput          – synthesize hardware mouse / keyboard events
///   • SetCursorPos       – move the system cursor
///   • EnumWindows / GetWindowText / GetWindowThreadProcessId – enumerate top-level windows
///   • PrintWindow        – capture a window's pixels (works even when occluded / off-screen)
///   • DwmGetWindowAttribute – obtain the real visible frame (DWM-aware)
///   • GetSystemMetrics   – measure primary monitor and virtual screen bounds
///
/// All declarations live in a single static class so the rest of the project
/// can reach for one well-known namespace and avoid scattering P/Invoke across files.
/// </summary>
internal static class Win32
{
    // =========================================================================
    //  SendInput — synthesized input
    // =========================================================================

    // INPUT.type values used by SendInput.
    public const uint INPUT_MOUSE    = 0;
    public const uint INPUT_KEYBOARD = 1;

    /// <summary>MOUSEINPUT.dwFlags bits — combine to describe a mouse event.</summary>
    [Flags]
    public enum MouseEventF : uint
    {
        Move        = 0x0001,
        LeftDown    = 0x0002,
        LeftUp      = 0x0004,
        RightDown   = 0x0008,
        RightUp     = 0x0010,
        MiddleDown  = 0x0020,
        MiddleUp    = 0x0040,
        Wheel       = 0x0800,
        HWheel      = 0x01000, // horizontal scroll wheel
        VirtualDesk = 0x4000,  // pair with Absolute so coords span ALL monitors, not just primary
        Absolute    = 0x8000,
    }

    /// <summary>KEYBDINPUT.dwFlags bits — combine to describe a keystroke.</summary>
    [Flags]
    public enum KeyEventF : uint
    {
        ExtendedKey = 0x0001,  // arrows, Ins/Del/Home/End, Win, etc.
        KeyUp       = 0x0002,  // omit for key-down
        Unicode     = 0x0004,  // wScan carries a Unicode code unit; wVk must be 0
        Scancode    = 0x0008,  // wScan carries a hardware scan code instead of a VK
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;            // x movement (absolute or relative — see Absolute flag)
        public int dy;            // y movement
        public uint mouseData;    // wheel delta or X-button id when applicable
        public uint dwFlags;      // MouseEventF combination
        public uint time;         // 0 = system supplies timestamp
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;        // virtual-key code (or 0 when sending Unicode)
        public ushort wScan;      // scan code or Unicode code unit
        public uint dwFlags;      // KeyEventF combination
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // INPUT is a tagged union; LayoutKind.Explicit gives us the C-style overlay.
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;     // INPUT_MOUSE / INPUT_KEYBOARD / INPUT_HARDWARE
        public INPUTUNION U;
    }

    /// <summary>
    /// Inject one or more synthesized input events into the system queue.
    /// The OS processes them as if they came from real hardware, so target
    /// applications cannot tell automation apart from a real user.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>Move the system cursor in physical pixels (PMv2 DPI-aware).</summary>
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    /// <summary>
    /// Translate a character to a virtual-key code (low byte) + shift state (high byte)
    /// for the current keyboard layout. Used to send chorded keys (Ctrl+S, Win+R) as
    /// real VK events so the OS hotkey/accelerator machinery recognizes them — Unicode
    /// injection (wVk=0) is invisible to VK-based hotkey handlers. Returns -1 on failure.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern short VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // =========================================================================
    //  Screen metrics — primary monitor and virtual screen (all monitors)
    // =========================================================================

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_CXSCREEN        = 0;   // primary monitor width
    public const int SM_CYSCREEN        = 1;   // primary monitor height
    public const int SM_XVIRTUALSCREEN  = 76;  // virtual-screen origin X (may be negative)
    public const int SM_YVIRTUALSCREEN  = 77;  // virtual-screen origin Y
    public const int SM_CXVIRTUALSCREEN = 78;  // virtual-screen total width
    public const int SM_CYVIRTUALSCREEN = 79;  // virtual-screen total height

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width  => Right - Left;
        public int Height => Bottom - Top;
    }

    // =========================================================================
    //  Window discovery and capture
    // =========================================================================

    /// <summary>Callback for <see cref="EnumWindows"/>. Return true to keep enumerating.</summary>
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>True when a window is minimized (iconic).</summary>
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // =========================================================================
    //  Window control — top-most, z-order, ex-styles, per-monitor DPI
    //  (used by the control_window / release_window tools + overlay frames)
    // =========================================================================

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    /// <summary>Set window position, size and z-order. Used to make a target top-most and to glue overlays above it.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // Special hWndInsertAfter values.
    public static readonly IntPtr HWND_TOP       = new(0);
    public static readonly IntPtr HWND_BOTTOM    = new(1);
    public static readonly IntPtr HWND_TOPMOST   = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // SetWindowPos flags.
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOZORDER   = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public const int GWL_EXSTYLE = -20;

    // Extended window styles used by the overlay frames.
    public const int WS_EX_TOPMOST     = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;   // click-through
    public const int WS_EX_TOOLWINDOW  = 0x00000080;   // no taskbar / alt-tab entry
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_NOACTIVATE  = 0x08000000;   // never steal activation

    /// <summary>Per-monitor DPI of a window (Win10 1607+). 96 = 100%.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>Resolves a window handle to the owning process id.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Legacy window rect (includes the ~7-pixel invisible resize border on Win10+).
    /// Prefer <see cref="DwmGetWindowAttribute"/> with
    /// <see cref="DWMWA_EXTENDED_FRAME_BOUNDS"/> for capture sizing.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// DWM extended frame bounds — the rectangle the user actually sees,
    /// excluding the invisible drop-shadow / resize border that GetWindowRect
    /// includes on Windows 10 and later.
    /// </summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>
    /// Render the given window's pixels into the supplied device context.
    /// Works for windows that are covered, partially off-screen, or even
    /// minimized (after a transient SW_RESTORE).
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    public const uint PW_CLIENTONLY        = 0x00000001;
    /// <summary>Capture DWM-composited surfaces (Chromium, UWP, WebView2, MAUI, etc.).</summary>
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    // =========================================================================
    //  Virtual-key constants (the subset KeySequenceParser maps by name)
    // =========================================================================

    /// <summary>Symbolic virtual-key codes. Mirrors the values in WinUser.h.</summary>
    public static class VK
    {
        public const ushort LBUTTON = 0x01, RBUTTON = 0x02, CANCEL = 0x03, MBUTTON = 0x04;
        public const ushort BACK = 0x08, TAB = 0x09, CLEAR = 0x0C, RETURN = 0x0D;
        public const ushort SHIFT = 0x10, CONTROL = 0x11, MENU = 0x12, PAUSE = 0x13, CAPITAL = 0x14;
        public const ushort ESCAPE = 0x1B;
        public const ushort SPACE = 0x20, PRIOR = 0x21, NEXT = 0x22, END = 0x23, HOME = 0x24;
        public const ushort LEFT = 0x25, UP = 0x26, RIGHT = 0x27, DOWN = 0x28;
        public const ushort PRINT = 0x2A, SNAPSHOT = 0x2C, INSERT = 0x2D, DELETE = 0x2E;
        public const ushort LWIN = 0x5B, RWIN = 0x5C, APPS = 0x5D;
        public const ushort F1 = 0x70, F12 = 0x7B, F24 = 0x87;
        public const ushort NUMLOCK = 0x90, SCROLL = 0x91;
        public const ushort LSHIFT = 0xA0, RSHIFT = 0xA1, LCONTROL = 0xA2, RCONTROL = 0xA3, LMENU = 0xA4, RMENU = 0xA5;
        public const ushort VOLUME_MUTE = 0xAD, VOLUME_DOWN = 0xAE, VOLUME_UP = 0xAF;
        public const ushort MEDIA_NEXT_TRACK = 0xB0, MEDIA_PREV_TRACK = 0xB1, MEDIA_STOP = 0xB2, MEDIA_PLAY_PAUSE = 0xB3;
    }
}
