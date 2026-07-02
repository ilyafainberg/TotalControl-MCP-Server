"""
NON-INTRUSIVE gate test for v1.5.0 enforcement.

With NO window under agent control, every gated tool must return an instruction
(⛔ ACTION BLOCKED ...) and perform NO desktop action. Because the tools are
blocked, nothing is clicked/typed/moved and no window is opened — safe to run
while the user is on the laptop.

We verify:
  • screenshot_screen        -> blocked (text instruction, no image)
  • screenshot_window(query)  -> blocked (target not controlled)
  • click_mouse / send_keys / scroll_mouse / drag_mouse / mouse_button -> blocked
  • click_in_window / find_element -> blocked
  • find_window               -> ALLOWED (bootstrap, returns list)
Then flip TOTALCONTROL_REQUIRE_WINDOW_SELECTION=0 in a second process and confirm
screenshot_screen is ALLOWED (returns an image) — proving the override works.
No window is ever controlled, so no overlay ever appears.
"""
import base64, json, os, subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
EXE  = os.path.join(HERE, "bin", "Debug", "net10.0-windows", "TotalControl.exe")
assert os.path.isfile(EXE), f"missing exe: {EXE}"

def run(env_extra):
    env = dict(os.environ); env.update(env_extra)
    p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, bufsize=0, env=env)
    st = {"n": 0}
    def s(m, pa=None, no=False):
        st["n"] += 1 if not no else 0
        msg = {"jsonrpc": "2.0", "method": m}
        if pa is not None: msg["params"] = pa
        if not no: msg["id"] = st["n"]
        p.stdin.write((json.dumps(msg) + "\n").encode("utf-8")); p.stdin.flush()
    def r():
        line = p.stdout.readline()
        if not line: raise RuntimeError("server died:\n" + p.stderr.read().decode(errors="replace"))
        return json.loads(line)
    def call(n, a):
        s("tools/call", {"name": n, "arguments": a})
        while True:
            m = r()
            if m.get("id") == st["n"]: return m
    def blocks(resp): return resp.get("result", {}).get("content", [])
    def is_blocked(resp):
        for c in blocks(resp):
            if c.get("type") == "text" and "ACTION BLOCKED" in c.get("text", ""):
                return True
        return False
    def has_image(resp): return any(c.get("type") == "image" for c in blocks(resp))

    s("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                     "clientInfo": {"name": "gate", "version": "0"}})
    ver = r()["result"]["serverInfo"]["version"]
    s("notifications/initialized", no=True)
    return p, call, is_blocked, has_image, ver

print("=== ENFORCEMENT ON (default) ===")
p, call, is_blocked, has_image, ver = run({})
print("server version:", ver)
fails = []

def expect_blocked(name, args):
    resp = call(name, args)
    ok = is_blocked(resp)
    print(f"  {name:18} -> {'BLOCKED ✅' if ok else 'NOT BLOCKED ❌'}")
    if not ok: fails.append(name)

expect_blocked("screenshot_screen", {})
expect_blocked("screenshot_window", {"query": "Notepad"})
expect_blocked("click_mouse", {"x": 400, "y": 400})
expect_blocked("send_keys", {"keys": "hello"})
expect_blocked("scroll_mouse", {"amount": -3})
expect_blocked("drag_mouse", {"startX": 100, "startY": 100, "endX": 200, "endY": 200})
expect_blocked("mouse_button", {"action": "down"})
expect_blocked("click_in_window", {"query": "Notepad", "imageX": 10, "imageY": 10})
expect_blocked("find_element", {"query": "Notepad", "name": "File"})

# find_window must be ALLOWED (bootstrap discovery, no capture)
fw = call("find_window", {})
fw_txt = "".join(c.get("text", "") for c in fw.get("result", {}).get("content", []))
fw_ok = "ACTION BLOCKED" not in fw_txt and ("window" in fw_txt.lower())
print(f"  {'find_window':18} -> {'ALLOWED ✅' if fw_ok else 'WRONGLY BLOCKED ❌'}")
if not fw_ok: fails.append("find_window(should be allowed)")
p.terminate()

print("\n=== ENFORCEMENT OFF (TOTALCONTROL_REQUIRE_WINDOW_SELECTION=0) ===")
p2, call2, is_blocked2, has_image2, ver2 = run({"TOTALCONTROL_REQUIRE_WINDOW_SELECTION": "0"})
resp = call2("screenshot_screen", {"scale": 0.25, "jpegQuality": 60})
off_ok = has_image2(resp) and not is_blocked2(resp)
print(f"  screenshot_screen (override) -> {'ALLOWED, image returned ✅' if off_ok else 'STILL BLOCKED ❌'}")
if not off_ok: fails.append("override failed")
p2.terminate()

print("\n" + ("ALL PASS ✅" if not fails else f"FAILURES: {fails}"))
