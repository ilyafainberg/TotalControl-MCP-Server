"""
Smoke-test the three new tools added in v1.2.0:
    click_in_window    image-relative click; should land precisely at window-origin + (rx, ry)
    hover_preview      cursor move + small crop with crosshair overlay
    find_element       UI Automation tree walk; finds + optionally invokes a control

Spawns TotalControl.exe (Debug) directly over stdio (raw JSON-RPC) so we don't
depend on Scout having reconnected to the MCP server.
"""

import base64
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "bin", "Debug", "net9.0-windows", "TotalControl.exe")
assert os.path.isfile(EXE), f"missing exe: {EXE}"

p = subprocess.Popen(
    [EXE],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    bufsize=0,
)

NEXT_ID = 0
def send(method, params=None, notify=False):
    global NEXT_ID
    NEXT_ID += 1
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None: msg["params"] = params
    if not notify: msg["id"] = NEXT_ID
    line = (json.dumps(msg) + "\n").encode("utf-8")
    p.stdin.write(line)
    p.stdin.flush()

def recv():
    line = p.stdout.readline()
    if not line:
        err = p.stderr.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"server died. stderr:\n{err}")
    return json.loads(line)

# Initialize
send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "0"}})
init = recv()
print(f"server: {init['result']['serverInfo']}")
send("notifications/initialized", notify=True)

def call(name, args):
    send("tools/call", {"name": name, "arguments": args})
    while True:
        msg = recv()
        if msg.get("id") == NEXT_ID:
            return msg

# List tools to confirm the new ones are exposed
send("tools/list", {})
tools = recv()["result"]["tools"]
names = sorted(t["name"] for t in tools)
print(f"tools exposed ({len(names)}): {names}")
required = {"click_in_window", "hover_preview", "find_element"}
missing = required - set(names)
assert not missing, f"MISSING new tools: {missing}"
print("[OK] all three new tools exposed.")

# Smoke test 1: hover_preview
print("\n--- hover_preview ---")
r = call("hover_preview", {"x": 200, "y": 200, "radius": 60})
res = r.get("result", r)
content = res.get("content", [])
for c in content:
    if c.get("type") == "text":
        print("  text:", c["text"][:200])
    elif c.get("type") == "image":
        b = base64.b64decode(c["data"])
        out = os.path.join(HERE, "_smoke_hover.png")
        with open(out, "wb") as f: f.write(b)
        print(f"  image: {len(b)} bytes → {out}")
assert any(c.get("type") == "image" for c in content), "hover_preview returned no image"
print("[OK] hover_preview returns image with crosshair.")

# Smoke test 2: find_element on Notepad — find File menu, don't invoke
print("\n--- find_element (notepad: find 'File' menuitem, no invoke) ---")
r = call("find_element", {
    "query": "Notepad",
    "name": "File",
    "controlType": "menuitem",
    "invoke": False,
    "maxResults": 5,
    "activate": True,
})
print("FULL RESPONSE:", json.dumps(r, indent=2)[:1500])

# Smoke test 3: click_in_window on Notepad - click somewhere safe in the text area
print("\n--- click_in_window (notepad: image (200, 200)) ---")
r = call("click_in_window", {
    "query": "Notepad",
    "imageX": 200,
    "imageY": 200,
    "button": "left",
    "activate": True,
})
print("FULL RESPONSE:", json.dumps(r, indent=2)[:1500])

# Clean shutdown
send("notifications/cancelled", {}, notify=True)
try:
    p.stdin.close()
except Exception: pass
p.wait(timeout=5)

# Drain stderr so we can see exception traces from the server.
stderr = p.stderr.read().decode("utf-8", errors="replace")
print("\n--- SERVER STDERR ---")
# Filter out the chatty Hosting lifecycle lines so the real exceptions stand out.
for line in stderr.splitlines():
    if "Microsoft.Hosting.Lifetime" in line: continue
    if "Application started" in line: continue
    if "Hosting environment" in line: continue
    if "Content root path" in line: continue
    print(line)
print("\n[DONE] smoke tests complete.")
