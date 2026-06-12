"""
Reads the current cursor position in PHYSICAL pixels and prints DPI info.
We declare PerMonitorV2 DPI awareness before any user32 calls so GetCursorPos
returns true physical pixels — matching what TotalControl's SetCursorPos uses.
"""
import ctypes
from ctypes import wintypes
import sys, json

# PerMonitorV2 = -4. Must be set before any user32 call that depends on awareness.
try:
    ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
except Exception:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # PerMonitor
    except Exception:
        ctypes.windll.user32.SetProcessDPIAware()

class POINT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]

pt = POINT()
ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))

# System metrics in physical pixels for primary + virtual screen
gm = ctypes.windll.user32.GetSystemMetrics
SM_CXSCREEN, SM_CYSCREEN = 0, 1
SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN = 76, 77
SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN = 78, 79

# DPI of the monitor under the cursor
MDT_EFFECTIVE_DPI = 0
MONITOR_DEFAULTTONEAREST = 2
hmon = ctypes.windll.user32.MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST)
dpi_x, dpi_y = wintypes.UINT(), wintypes.UINT()
try:
    ctypes.windll.shcore.GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI,
                                           ctypes.byref(dpi_x), ctypes.byref(dpi_y))
    dpi = (dpi_x.value, dpi_y.value)
except Exception:
    dpi = (96, 96)

out = {
    "cursor_phys": [pt.x, pt.y],
    "primary": [gm(SM_CXSCREEN), gm(SM_CYSCREEN)],
    "virtual": [gm(SM_XVIRTUALSCREEN), gm(SM_YVIRTUALSCREEN),
                gm(SM_CXVIRTUALSCREEN), gm(SM_CYVIRTUALSCREEN)],
    "dpi": list(dpi),
    "scale": dpi[0] / 96.0,
}
print(json.dumps(out))
