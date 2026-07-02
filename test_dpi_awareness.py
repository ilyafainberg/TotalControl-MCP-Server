"""
Probe the ACTUAL DPI awareness of the running TotalControl.exe process.
If it reports PER_MONITOR_AWARE_V2 (value 'context' resolving to awareness 2),
SetCursorPos uses physical pixels -> correct. If it reports UNAWARE (0),
the manifest didn't take and the cursor lands wrong on scaled monitors.
"""
import ctypes, subprocess, time, os
from ctypes import wintypes

ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

EXE = r"C:\Users\ifain\OneDrive - Feincraft\Projects\TotalControl\bin\Debug\net10.0-windows\TotalControl.exe"

proc = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
time.sleep(1.5)

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
h = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, proc.pid)
if not h:
    print("OpenProcess failed", ctypes.GetLastError())
else:
    # GetProcessDpiAwarenessInternal(hProcess, &value): 0=unaware,1=system,2=permonitor
    value = ctypes.c_int(-1)
    ok = ctypes.windll.user32.GetProcessDpiAwarenessInternal(h, ctypes.byref(value))
    names = {0: "UNAWARE (BAD - cursor will be wrong on scaled monitors)",
             1: "SYSTEM_AWARE (partial)",
             2: "PER_MONITOR_AWARE (GOOD)"}
    print(f"GetProcessDpiAwarenessInternal ok={ok} value={value.value} -> {names.get(value.value, '?')}")
    ctypes.windll.kernel32.CloseHandle(h)

proc.terminate()
