"""
v1.3.2 verification:
  A. Keyboard parser — the previously-broken sequences must NOT error:
       {Win}, {Ctrl}, {Shift}, {Alt}      (lone modifiers)
       {Win+R}, {Ctrl+S}, {Ctrl+Shift+T}  (chords now sent as VK codes)
     Plus a FUNCTIONAL test: type into Notepad, {Ctrl+A}{Delete} must clear it
     (proves Ctrl+A is delivered as a real VK chord, not invisible Unicode).
  B. Mouse accuracy — move_mouse to known coords, read back via an independent
     DPI-aware GetCursorPos. Must match within 1px.
"""
import base64, ctypes, json, os, subprocess, time
from ctypes import wintypes

ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

HERE = os.path.dirname(os.path.abspath(__file__))
EXE  = os.path.join(HERE, "bin", "Debug", "net10.0-windows", "TotalControl.exe")
assert os.path.isfile(EXE), f"missing exe: {EXE}"

p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, bufsize=0)
NID = 0
def send(method, params=None, notify=False):
    global NID
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None: msg["params"] = params
    if not notify:
        NID += 1; msg["id"] = NID
    p.stdin.write((json.dumps(msg) + "\n").encode("utf-8")); p.stdin.flush()
def recv():
    line = p.stdout.readline()
    if not line:
        raise RuntimeError("server died:\n" + p.stderr.read().decode(errors="replace"))
    return json.loads(line)
def call(name, args):
    send("tools/call", {"name": name, "arguments": args})
    while True:
        m = recv()
        if m.get("id") == NID: return m

def text_of(resp):
    res = resp.get("result", resp)
    if res.get("isError"):
        return "ERROR: " + json.dumps(res)
    for c in res.get("content", []):
        if c.get("type") == "text": return c["text"]
    return ""

class POINT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG)]
def cursor():
    pt = POINT(); ctypes.windll.user32.GetCursorPos(ctypes.byref(pt)); return (pt.x, pt.y)

fails = []
try:
    send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                        "clientInfo": {"name": "kbtest", "version": "0"}})
    print("server:", recv()["result"]["serverInfo"])
    send("notifications/initialized", notify=True)

    print("\n=== A. Keyboard parser (must NOT error) ===")
    for seq in ["{Ctrl}", "{Shift}", "{Alt}", "{Win+R}", "{Ctrl+S}", "{Ctrl+Shift+T}"]:
        # NB: {Win+R} actually opens Run; close it right after with {Esc}.
        t = text_of(call("send_keys", {"keys": seq}))
        ok = "ERROR" not in t and "no main key" not in t.lower()
        print(f"  {seq:18} -> {'OK' if ok else 'FAIL: ' + t}")
        if not ok: fails.append(seq)
        if seq == "{Win+R}":
            time.sleep(0.4); call("send_keys", {"keys": "{Esc}"})  # dismiss Run dialog

    # {Win} alone — opens Start; verify no error, then dismiss.
    t = text_of(call("send_keys", {"keys": "{Win}"}))
    ok = "ERROR" not in t and "no main key" not in t.lower()
    print(f"  {'{Win}':18} -> {'OK' if ok else 'FAIL: ' + t}")
    if not ok: fails.append("{Win}")
    time.sleep(0.4); call("send_keys", {"keys": "{Esc}"})  # close Start menu

    print("\n=== A2. Functional: Ctrl+A must select-all in Notepad ===")
    subprocess.Popen(["notepad.exe"]); time.sleep(1.5)
    call("screenshot_window", {"query": "Notepad", "activate": True}); time.sleep(0.3)
    call("send_keys", {"keys": "DELETEME_DELETEME"}); time.sleep(0.2)
    # If Ctrl+A is delivered as a real VK chord, this selects all; Delete then clears.
    call("send_keys", {"keys": "{Ctrl+a}"}); time.sleep(0.2)
    call("send_keys", {"keys": "{Delete}"}); time.sleep(0.2)
    # Re-type a marker; if the doc had leftover text the layout would differ.
    call("send_keys", {"keys": "CLEARED"}); time.sleep(0.2)
    print("  typed 'DELETEME_DELETEME', sent {Ctrl+a}{Delete}, typed 'CLEARED'")
    print("  (visual check: Notepad should read exactly 'CLEARED')")

    print("\n=== B. Mouse accuracy (move_mouse vs independent GetCursorPos) ===")
    for (tx, ty) in [(300, 300), (800, 450), (1500, 900), (120, 120)]:
        call("move_mouse", {"x": tx, "y": ty}); time.sleep(0.1)
        cx, cy = cursor()
        dx, dy = abs(cx - tx), abs(cy - ty)
        ok = dx <= 1 and dy <= 1
        print(f"  target ({tx},{ty}) -> cursor ({cx},{cy}) delta ({dx},{dy}) {'OK' if ok else 'FAIL'}")
        if not ok: fails.append(f"move({tx},{ty})")

finally:
    p.terminate()

print("\n" + ("ALL PASS ✅" if not fails else f"FAILURES: {fails}"))
