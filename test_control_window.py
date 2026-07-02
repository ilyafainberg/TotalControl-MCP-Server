"""
Interactive test for control_window / release_window (v1.4.0).

  1. Open a throwaway Notepad.
  2. control_window('Notepad')  -> Notepad goes top-most, crimson frame + tag appear.
  3. screenshot_screen (full desktop composite) -> captures the overlay as proof.
  4. release_window('Notepad')  -> frame removed, top-most reverted.
  5. screenshot_screen again -> frame should be gone.

Full-desktop capture (CopyFromScreen) is used deliberately: PrintWindow grabs only
the target's own pixels, but the overlay is a SEPARATE window on top, so only a
composite desktop grab shows it.
"""
import base64, json, os, subprocess, time

HERE = os.path.dirname(os.path.abspath(__file__))
EXE  = os.path.join(HERE, "bin", "Debug", "net10.0-windows", "TotalControl.exe")
assert os.path.isfile(EXE), f"missing exe: {EXE}"

p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, bufsize=0)
N = 0
def s(m, pa=None, no=False):
    global N
    msg = {"jsonrpc": "2.0", "method": m}
    if pa is not None: msg["params"] = pa
    if not no: N += 1; msg["id"] = N
    p.stdin.write((json.dumps(msg) + "\n").encode("utf-8")); p.stdin.flush()
def r():
    line = p.stdout.readline()
    if not line: raise RuntimeError("server died:\n" + p.stderr.read().decode(errors="replace"))
    return json.loads(line)
def call(n, a):
    s("tools/call", {"name": n, "arguments": a})
    while True:
        m = r()
        if m.get("id") == N: return m
def text_of(resp):
    for c in resp.get("result", {}).get("content", []):
        if c.get("type") == "text": return c["text"]
    return json.dumps(resp)
def save_shot(resp, path):
    for c in resp.get("result", {}).get("content", []):
        if c.get("type") == "image":
            open(path, "wb").write(base64.b64decode(c["data"])); return True
    return False

try:
    s("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                     "clientInfo": {"name": "ctl", "version": "0"}})
    print("server:", r()["result"]["serverInfo"])
    s("notifications/initialized", no=True)

    print("\n1. use the already-open Notepad (no new launch)")

    print("2. control_window('Notepad')  [retry up to 3×]")
    ctl_ok = False
    for attempt in range(3):
        t = text_of(call("control_window", {"query": "Notepad"}))
        if "error" not in t.lower():
            ctl_ok = True
            print("  ", t.replace("\n", "\n   "))
            break
        print(f"   attempt {attempt+1} failed, retrying…"); time.sleep(1.0)
    if not ctl_ok:
        print("   control_window failed all attempts"); raise SystemExit
    time.sleep(0.9)  # let the overlay paint + follow-timer settle

    print("3. screenshot_screen (proof of frame)")
    shot = call("screenshot_screen", {"jpegQuality": 80})
    print("  ", text_of(shot))
    save_shot(shot, os.path.join(HERE, "_ctl_controlled.jpg"))
    print("   saved _ctl_controlled.jpg")

    print("4. release_window('Notepad')")
    print("  ", text_of(call("release_window", {"query": "Notepad"})))
    time.sleep(0.6)

    print("5. screenshot_screen (frame should be gone)")
    shot2 = call("screenshot_screen", {"jpegQuality": 80})
    save_shot(shot2, os.path.join(HERE, "_ctl_released.jpg"))
    print("   saved _ctl_released.jpg")

    print("\n[DONE]")
finally:
    p.terminate()
    err = p.stderr.read().decode(errors="replace")
    bad = [l for l in err.splitlines() if "fail:" in l.lower() or "Exception" in l]
    if bad:
        print("\n--- STDERR ERRORS ---")
        for l in bad: print("  ", l)
