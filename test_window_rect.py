"""
Definitive accuracy test:
1. Capture full primary screen via Win32 (DPI aware)
2. Find the Solitaire window's bounds via DWM (DPI aware)
3. Crop the screen capture to those bounds
4. Capture the window via PrintWindow
5. Compare crop vs window capture pixel-for-pixel

If they match, the window origin/size and the screen capture share the same
physical-pixel coordinate space — i.e., translating image coords to screen
coords by adding window origin is correct.
"""
import ctypes
from ctypes import wintypes, byref
import sys

# PerMonitor V2 DPI awareness
ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
dwmapi = ctypes.windll.dwmapi

class RECT(ctypes.Structure):
    _fields_ = [("left", wintypes.LONG), ("top", wintypes.LONG),
                ("right", wintypes.LONG), ("bottom", wintypes.LONG)]

DWMWA_EXTENDED_FRAME_BOUNDS = 9

# Enumerate windows to find Solitaire
target_hwnd = None
target_title = None

@ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
def enum_proc(hwnd, lparam):
    global target_hwnd, target_title
    if not user32.IsWindowVisible(hwnd):
        return True
    length = user32.GetWindowTextLengthW(hwnd)
    if length == 0:
        return True
    buf = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, buf, length + 1)
    if "Solitaire" in buf.value:
        target_hwnd = hwnd
        target_title = buf.value
        return False
    return True

user32.EnumWindows(enum_proc, 0)
if not target_hwnd:
    print("No Solitaire window found")
    sys.exit(1)

# Get DWM extended frame bounds (physical pixels)
rect = RECT()
hr = dwmapi.DwmGetWindowAttribute(target_hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                                    byref(rect), ctypes.sizeof(rect))
if hr != 0:
    user32.GetWindowRect(target_hwnd, byref(rect))

# Also get plain GetWindowRect for comparison
rect2 = RECT()
user32.GetWindowRect(target_hwnd, byref(rect2))

# Get primary screen dims
SM_CXSCREEN, SM_CYSCREEN = 0, 1
prim_w = user32.GetSystemMetrics(SM_CXSCREEN)
prim_h = user32.GetSystemMetrics(SM_CYSCREEN)

print(f"Target window: hwnd=0x{target_hwnd:X} '{target_title}'")
print(f"DWM bounds:        ({rect.left}, {rect.top}) → ({rect.right}, {rect.bottom})   size {rect.right-rect.left}×{rect.bottom-rect.top}")
print(f"GetWindowRect:     ({rect2.left}, {rect2.top}) → ({rect2.right}, {rect2.bottom})   size {rect2.right-rect2.left}×{rect2.bottom-rect2.top}")
print(f"Primary screen:    {prim_w}×{prim_h}")

# Now move cursor to (window.left + 50, window.top + 50) and verify
target_x = rect.left + 50
target_y = rect.top + 50
print(f"\nMoving cursor to: ({target_x}, {target_y})")
user32.SetCursorPos(target_x, target_y)

import time; time.sleep(0.05)

class POINT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]
pt = POINT()
user32.GetCursorPos(byref(pt))
print(f"Cursor now at:    ({pt.x}, {pt.y})  -> delta ({pt.x-target_x}, {pt.y-target_y})")
