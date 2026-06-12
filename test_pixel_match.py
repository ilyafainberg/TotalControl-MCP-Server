"""
End-to-end pixel-accuracy test.

This script:
  1. Saves the current cursor position so we can restore it.
  2. Drives TotalControl via raw JSON-RPC stdio to grab:
        a. A full-screen PNG
        b. A window PNG of the Solitaire window  (records reported origin/size)
  3. Crops the full-screen PNG to the reported window rect.
  4. Pixel-diffs the crop vs the window PNG.

If the diff is zero (or just AA noise on tiny areas), the screenshot grid IS
the same physical-pixel coordinate space as move_mouse — no DPI scaling bug.
"""
import json, subprocess, base64, io, struct, sys, os, time, ctypes
from ctypes import wintypes, byref

ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))

EXE = r"C:\Users\ifain\OneDrive - Microsoft\Power CAT\TotalControl\bin\Debug\net9.0-windows\TotalControl.exe"

def rpc(proc, req):
    line = json.dumps(req) + "\n"
    proc.stdin.write(line)
    proc.stdin.flush()
    # Drain until we get matching id
    while True:
        resp_line = proc.stdout.readline()
        if not resp_line:
            raise RuntimeError("EOF")
        try:
            resp = json.loads(resp_line)
        except json.JSONDecodeError:
            continue
        if resp.get("id") == req.get("id"):
            return resp

proc = subprocess.Popen(
    [EXE],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
    text=True, encoding='utf-8', bufsize=1,
)

try:
    # initialize
    rpc(proc, {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
        "protocolVersion":"2024-11-05",
        "capabilities":{},
        "clientInfo":{"name":"test","version":"1"}}})
    proc.stdin.write(json.dumps({"jsonrpc":"2.0","method":"notifications/initialized"})+"\n")
    proc.stdin.flush()

    # full screen
    full = rpc(proc, {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{
        "name":"screenshot_screen","arguments":{}}})
    # window
    win = rpc(proc, {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{
        "name":"screenshot_window","arguments":{"query":"Solitaire"}}})
finally:
    proc.terminate()

def extract(resp):
    blocks = resp["result"]["content"]
    text = next((b["text"] for b in blocks if b.get("type")=="text"), "")
    img  = next((b for b in blocks if b.get("type")=="image"), None)
    return text, base64.b64decode(img["data"])

full_text, full_png = extract(full)
win_text,  win_png  = extract(win)
print("FULL:", full_text)
print("WIN: ", win_text)

OUT = r"C:\Users\ifain\OneDrive - Microsoft\Power CAT\TotalControl"
with open(os.path.join(OUT, "_test_full.png"),  "wb") as f: f.write(full_png)
with open(os.path.join(OUT, "_test_win.png"),   "wb") as f: f.write(win_png)

# Parse window text for origin: "...at (X,Y)."
import re
m = re.search(r"(\d+)×(\d+) at \((-?\d+),(-?\d+)\)", win_text)
W, H, X, Y = (int(g) for g in m.groups())
print(f"\nParsed window: {W}×{H} at ({X}, {Y})")

# Decode PNGs with PIL if available; else just check dims via PNG header.
try:
    from PIL import Image, ImageChops
    full_im = Image.open(io.BytesIO(full_png))
    win_im  = Image.open(io.BytesIO(win_png))
    print(f"Full PNG: {full_im.size}, mode={full_im.mode}")
    print(f"Win  PNG: {win_im.size}, mode={win_im.mode}")
    crop = full_im.crop((X, Y, X+W, Y+H)).convert(win_im.mode)
    if crop.size != win_im.size:
        print(f"!! Crop size {crop.size} != window size {win_im.size}")
    else:
        diff = ImageChops.difference(crop, win_im)
        bbox = diff.getbbox()
        if bbox is None:
            print("PIXEL MATCH: crop == window (no differing pixels)")
        else:
            # measure max delta
            stat_min, stat_max = diff.getextrema() if win_im.mode != "RGB" else (None, None)
            print(f"DIFF bbox: {bbox}")
            if win_im.mode in ("RGB","RGBA"):
                # per-channel extrema is a tuple of (min,max) per channel
                ext = diff.getextrema()
                print("Per-channel diff extrema:", ext)
            # save diff visualization
            diff.save(os.path.join(OUT, "_test_diff.png"))
            crop.save(os.path.join(OUT, "_test_crop.png"))
            print("Saved _test_diff.png and _test_crop.png next to _test_full/_test_win.png")
except ImportError:
    # Fall back to manual PNG width/height read (IHDR at byte 16..23)
    def png_size(b): return struct.unpack(">II", b[16:24])
    print(f"Full PNG dims: {png_size(full_png)}")
    print(f"Win  PNG dims: {png_size(win_png)}")
