"""
Smoke-test drag_mouse and scroll_mouse (v1.3.0 additions/fixes).

Drags the cursor from (200,200) to (600,400) and checks the endpoint
lands correctly. Scrolls the mouse wheel and checks we get a success message.
Also verifies scroll_mouse is in the tools list.
"""

import json, os, subprocess, time, sys

HERE = os.path.dirname(os.path.abspath(__file__))
EXE  = os.path.join(HERE, "bin", "Debug", "net10.0-windows", "TotalControl.exe")
assert os.path.isfile(EXE), f"missing exe: {EXE}"

p = subprocess.Popen(
    [EXE],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    bufsize=0,
)

ID = 0
def send(method, params=None, notify=False):
    global ID
    req = {"jsonrpc": "2.0", "method": method}
    if not notify:
        ID += 1
        req["id"] = ID
    if params:
        req["params"] = params
    p.stdin.write((json.dumps(req) + "\n").encode())
    p.stdin.flush()
    if notify:
        return None
    while True:
        line = p.stdout.readline().decode()
        if not line:
            break
        try:
            r = json.loads(line)
            if r.get("id") == req["id"]:
                return r
        except json.JSONDecodeError:
            pass

try:
    send("initialize", {"protocolVersion": "2024-11-05",
                        "clientInfo": {"name": "drag-test", "version": "0"}}, notify=False)
    send("notifications/initialized", notify=True)

    # --- tools list ---
    tools_resp = send("tools/list")
    names = [t["name"] for t in tools_resp["result"]["tools"]]
    print(f"tools exposed ({len(names)}): {names}")
    assert "drag_mouse"   in names, "drag_mouse missing!"
    assert "scroll_mouse" in names, "scroll_mouse missing!"
    print("[OK] drag_mouse + scroll_mouse present in tools/list")

    # --- drag_mouse ---
    print("\n--- drag_mouse (200,200) -> (600,400) ---")
    r = send("tools/call", {"name": "drag_mouse", "arguments": {
        "startX": 200, "startY": 200,
        "endX":   600, "endY":   400,
        "steps": 30, "downDelayMs": 20, "holdAfterMs": 50,
    }})
    text = r["result"]["content"][0]["text"]
    print(" ", text)
    assert "error" not in text.lower() or "isError" not in str(r.get("result",{})), \
        f"drag returned error: {r}"
    assert "(600, 400)" in text or "(600,400)" in text, \
        f"cursor didn't land at (600,400): {text}"
    print("[OK] drag landed at (600,400)")

    # --- scroll_mouse ---
    print("\n--- scroll_mouse 3 notches down ---")
    r = send("tools/call", {"name": "scroll_mouse", "arguments": {
        "amount": -3,
    }})
    text = r["result"]["content"][0]["text"]
    print(" ", text)
    assert "3" in text and "down" in text.lower(), f"unexpected scroll result: {text}"
    print("[OK] scroll_mouse OK")

    # --- scroll horizontal ---
    print("\n--- scroll_mouse 2 notches right (horizontal) ---")
    r = send("tools/call", {"name": "scroll_mouse", "arguments": {
        "amount": 2, "horizontal": True, "x": 400, "y": 300,
    }})
    text = r["result"]["content"][0]["text"]
    print(" ", text)
    assert "right" in text.lower(), f"unexpected horizontal scroll result: {text}"
    print("[OK] horizontal scroll OK")

finally:
    p.terminate()
    stderr = p.stderr.read().decode(errors="replace")
    # Print only error lines
    errs = [l for l in stderr.splitlines() if "fail:" in l.lower() or "error" in l.lower()]
    if errs:
        print("\n--- STDERR ERRORS ---")
        for e in errs: print(" ", e)

print("\n[DONE] drag + scroll smoke tests complete.")
