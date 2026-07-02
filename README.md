# TotalControl MCP Server

> A Model Context Protocol (MCP) server that gives an AI agent
> **hardware-level control of a Windows desktop** — synthesized mouse,
> keyboard, screen capture, and UI-Automation introspection — so it can
> drive any application that has no API.

Built for **[Microsoft Scout](https://aka.ms/scout)** and any other
MCP-compatible host (Claude Desktop, GitHub Copilot CLI, Continue, Cline,
…).

- **License:** MIT
- **Stack:** .NET 10 · C# · `ModelContextProtocol` SDK · Win32 `SendInput` / `PrintWindow` · UI Automation · WPF overlays · FFmpeg (optional, for recording)

---

## ⚠️ WARNING — Read this first

**TotalControl gives an AI agent the same control over your computer that
you have with a mouse and keyboard. There is no sandbox.** The agent can
click anything, type anywhere, drag-and-drop files, dismiss dialogs, send
chats, submit forms, and run commands — on *your* logged-in user account,
with *your* permissions, against *your* open applications.

By installing and running this software you accept the following:

- **USE AT YOUR OWN RISK.** No safety net protects you from a mistaken
  click, a hallucinated key sequence, or a prompt-injection attack that
  redirects the agent.
- **NO WARRANTY.** This software is provided **"AS IS"**, without warranty
  of any kind, express or implied — including merchantability, fitness for
  a particular purpose, and non-infringement. See [LICENSE](LICENSE).
- **NO LIABILITY.** The author is not liable for any damages — lost data,
  deleted files, sent messages, financial transactions, leaked secrets,
  broken systems — arising from use of this software.
- **NOT A MICROSOFT PRODUCT.** TotalControl is a personal open-source
  project. It is not supported, endorsed, or maintained by Microsoft. No
  Microsoft support channel will help you with it.
- **YOU ARE THE OPERATOR.** Always supervise the agent. Never leave it
  unattended while logged into accounts that can spend money, send mail,
  delete data, or change system state.

### Recommended precautions

- Run it inside a **dedicated user account, VM, or sandbox VM** — never on
  a machine where a stray click could cost you data or money.
- **Sign out of banking, brokerage, and cloud-billing dashboards** before
  starting an agent session.
- Keep **shells with elevated privileges (admin PowerShell, sudo)** closed
  while the agent is active — `send_keys` types into whatever window has
  focus, and an "untrusted" web page in the foreground can become the
  target.
- Treat any text the agent reads from the screen (email, web page, chat
  message, document) as **untrusted input** that may try to manipulate
  it. Configure your MCP host to require user confirmation for any
  destructive action (delete, send, submit, format, shutdown).
- Review the agent's tool calls before allowing them. Most MCP hosts can
  display each tool invocation for approval — **leave that on**.

---

## What it does

Most automation tools (Selenium, Playwright)
target a specific surface. **TotalControl is surface-agnostic** — if a
pixel can be drawn and an input can be received, an agent can read and
write it:

- Native Win32, WPF, WinForms, UWP, MAUI
- Electron / Chromium / WebView2 (Teams, VS Code, Slack, Discord, Notion)
- Legacy LOB apps, installer wizards, configuration dialogs
- Virtual-machine consoles, Citrix / RDP sessions
- Games, kiosk shells, anything Windows can render

The agent runs the classic **see → think → act → verify** loop:
`screenshot` → reason → `move_mouse` / `click_mouse` / `send_keys` →
`screenshot` again.

For accessibility-aware apps the agent can skip pixels entirely and use
`find_element` to ask the UI Automation tree for a control by name.

---

## Window control & the safety model

Two problems plague desktop agents: they **click the wrong window** (the
target was in the background and something else was on top at those
coordinates), and the user has **no way to see or stop** what the agent is
doing. TotalControl solves both with a mandatory *window selection* model.

- **`control_window(query)`** pins a window **top-most**, brings it
  forward, and draws a **crimson border** around it plus an **"Under Agent
  Control"** tag with an **✕** button above its titlebar. Now the window the
  agent screenshots and clicks is guaranteed to be the one on top, and the
  user can *see* exactly which window is being driven.
- **Enforcement gate (on by default).** Capture and interaction tools
  **refuse to run until a window is selected** — they return an instruction
  telling the agent to call `find_window` → `control_window` first. Absolute
  tools (`screenshot_screen`, `click_mouse`, `send_keys`, …) require at
  least one controlled window; window-scoped tools (`screenshot_window`,
  `click_in_window`, `find_element`, `crop_screenshot`, `record_window`)
  require *that* window to be controlled. Disable with the env var
  `TOTALCONTROL_REQUIRE_WINDOW_SELECTION=0`.
- **User abort.** Clicking the **✕** on the frame is an explicit **STOP**:
  it cancels any in-flight operation (e.g. a recording) and the agent's next
  tool call returns an `⛔ ABORTED BY USER` message instructing it to halt
  and ask how to proceed.
- **`release_window(query | all=true)`** removes the frame and restores the
  window's original (non-top-most) state. The agent **must** release when
  done; a process-exit safety-net un-top-mosts anything left behind.

```text
find_window                                  # discover windows (text, no capture)
control_window query="Calculator"            # select + frame it (goes top-most)
screenshot_window query="Calculator"         # now permitted
click_in_window  query="Calculator" imageX=40 imageY=220
release_window   query="Calculator"          # REQUIRED when finished
```

---

## Tools (16)

### Window control (start here)

| Tool | Purpose |
|---|---|
| `find_window` | List visible top-level windows (title, process, PID, position) as **text** — no screenshot, always allowed. Use it to discover targets. |
| `control_window` | **Select** a window: pin it top-most, bring it forward, draw the crimson "Under Agent Control" frame + ✕ button. Required before capture/interaction (enforced). |
| `release_window` | Remove the frame and restore the window's original state. Pass `all=true` to release everything. |
| `record_window` | Screen-record a controlled window to an **MP4** (H.264) for a fixed duration. Needs FFmpeg. Abortable via ✕. |

### Input

| Tool | Purpose |
|---|---|
| `move_mouse` | Move the cursor to absolute screen coordinates (physical pixels). |
| `click_mouse` | Synthesize left / right / middle click at the current or given position. Supports double-click. |
| `mouse_button` | Press OR release a single button (low-level drag primitive). Use when you need a button held while the cursor moves. |
| `drag_mouse` | Atomic drag: press at `(startX, startY)` → glide through intermediate `WM_MOUSEMOVE` events → release at `(endX, endY)`. Works for drag-and-drop, marquee selection, sliders, paint strokes, card games. |
| `scroll_mouse` | Scroll the mouse wheel (vertical or horizontal) at the current or a given position. |
| `send_keys` | Type text and named keys / chords into whichever window has focus. |

### Vision

| Tool | Purpose |
|---|---|
| `screenshot_screen` | Capture the primary monitor (or full virtual screen across all displays). Supports `scale` + `jpegQuality` to cut token cost. |
| `screenshot_window` | Capture a specific app window by title or process name — even if covered or minimized. Supports `scale` + `jpegQuality`. |
| `crop_screenshot` | Capture only a sub-region of the screen or a window (region-of-interest) — the cheapest way to read one dialog/element. |
| `hover_preview` | Move the cursor to `(x, y)`, then return a small crop with a yellow+black crosshair drawn at the target. Verify you're about to click the right pixel **before** committing. |

### High-level

| Tool | Purpose |
|---|---|
| `click_in_window` | Click at *window-relative* coordinates (e.g. "the pixel at (200, 80) inside Notepad"). The server resolves the window's screen origin and does the math — eliminates a whole class of "operator error" coordinate bugs. |
| `find_element` | Walk the UI Automation tree of a window. Find a control by Name / AutomationId / ControlType, return its bounding rect and center, optionally activate it via the accessibility layer (Invoke / Toggle / SelectionItem / ExpandCollapse). Bypasses the synthetic-input restrictions that block clicks on UWP / AppContainer apps. |

### Performance knobs (screenshots)

- `scale=0.5` — halve both dimensions (~4× smaller file); great for navigation shots.
- `jpegQuality=75` — JPEG instead of PNG (~15× smaller); good for reading text.
- `crop_screenshot` — capture only the region of interest.

### `send_keys` syntax

| Pattern | Meaning | Example |
|---|---|---|
| `text` | Unicode text (IME-aware, no shift-state guessing) | `Hello, world!` |
| `{Key}` | Named virtual key | `{Enter}` `{Tab}` `{Esc}` `{Backspace}` `{F5}` `{Win}` |
| `{Mod+Key}` | Chord with modifiers `Ctrl`, `Alt`, `Shift`, `Win` | `{Ctrl+S}` `{Ctrl+Shift+T}` `{Alt+F4}` `{Win+R}` |
| `{Key N}` | Repeat a key N times | `{Tab 5}` `{Backspace 10}` |
| `{{` / `}}` | Literal `{` / `}` | `{{not a token}}` |

Supported named keys: `Enter`, `Return`, `Tab`, `Esc`/`Escape`, `Space`,
`Backspace`/`Bksp`, `Delete`/`Del`, `Insert`/`Ins`, `Up`, `Down`, `Left`,
`Right`, `Home`, `End`, `PageUp`/`PgUp`, `PageDown`/`PgDn`, `CapsLock`,
`NumLock`, `ScrollLock`, `PrintScreen`/`PrtSc`, `Pause`, `Win`/`LWin`,
`RWin`, `Apps`, `F1`–`F24`, `VolumeUp`, `VolumeDown`, `VolumeMute`,
`Play`, `NextTrack`, `PrevTrack`.

### Cheat-sheet examples

```text
# Always select the window first (enforced):
find_window filter="Notepad"
control_window query="Notepad"

send_keys "Save the file{Ctrl+S}"
send_keys "{Win+R}notepad{Enter}"
send_keys "{Ctrl+A}{Delete}New content here.{Enter}"

click_mouse button=right x=820 y=440
click_in_window query="Notepad" imageX=200 imageY=80     # window-relative
hover_preview x=820 y=440 radius=60                       # verify-before-click
scroll_mouse amount=-5                                    # scroll down 5 notches

drag_mouse startX=120 startY=400 endX=540 endY=400        # marquee / drag-drop
mouse_button action=down button=left x=120 y=400          # low-level grab
mouse_button action=up   button=left                      # …release

screenshot_window query="Notepad" scale=0.5 jpegQuality=75
crop_screenshot window="Notepad" x=200 y=150 width=400 height=200
find_element query="Notepad" name="File" controlType="menuitem" invoke=true
record_window query="Notepad" durationSeconds=10 fps=15   # → MP4

release_window all=true                                   # REQUIRED when done
```

### Recording windows to MP4

`record_window` captures a **controlled** window to an H.264 MP4 using the
same DWM-aware capture as `screenshot_window`, piped to FFmpeg:

```text
control_window query="Visual Studio Code"
record_window  query="Visual Studio Code" durationSeconds=20 fps=15
# → %USERPROFILE%\Videos\TotalControl\<title>_<timestamp>.mp4
```

- Requires **FFmpeg** on `PATH` (or set `TOTALCONTROL_FFMPEG` to its full
  path). Install: `winget install Gyan.FFmpeg`.
- The call **blocks** for `durationSeconds` (1–300). The "Under Agent
  Control" frame is **not** captured — only the window's own pixels.
- Clicking **✕** on the frame stops the recording early; the partial MP4 is
  still finalized.

---

## Use cases

### 1. Drive apps without an API

QuickBooks, SAP GUI, legacy WinForms LOB apps, installer wizards — if it
runs on Windows, the agent can drive it. No "connector required",
no "use the REST API instead", no extension points.

> *"Open the admin console, switch to the Production environment, export
> the list of records to a CSV, save it to my Downloads folder, then mail
> it to my manager."*

### 2. Bridge two apps that don't know about each other

A SaaS web app + a 90's desktop accounting tool. An incoming Teams chat +
an Excel spreadsheet. A PDF + a CRM. The agent reads from one, decides,
and writes to the other.

> *"Watch the #orders Teams channel. For every new line item, open the
> ERP, add the SKU, and confirm. Take a screenshot before each confirm
> and ask me to approve."*

### 3. End-to-end QA / smoke tests of any UI

Drive an app the way a human user would, take screenshots between steps,
and have the agent compare them against an expected baseline. Works for
WPF, WinForms, UWP, MAUI, WebView2, Electron — the whole stack at once.

> *"Walk the new-user signup flow in our installer. Screenshot every
> step. Tell me if any dialog shows an unexpected error or layout
> regression."*

### 4. Demos & screencasts that don't depend on the demo gods

Record a video of an agent walking through a feature, in any app,
zero-touch. If the demo crashes, the agent recovers; if a popup steals
focus, the agent dismisses it.

> *"Demo the new Copilot prompt-library feature in Word. Open Word,
> create a doc, invoke Copilot, paste this prompt, save the doc, screen
> record the whole thing."*

### 5. Accessibility & remote-help

A power user runs the agent and dictates steps; the agent does the
clicking and typing for a person who can't. Or a remote helper sends a
prompt to a household member's machine instead of using a screen-share
tool.

### 6. Long-running back-office workflows

End-of-month report generation across 12 web dashboards and three
desktop apps; nightly data exports out of a legacy system; bulk renames,
file reorganizations, image conversions.

### 7. Game and kiosk automation

Drive turn-based games, click through onboarding wizards, walk through
kiosk shells. The included v1.2.0 `find_element` works around the
synthetic-input restrictions that previously blocked clicks on UWP apps
like Microsoft Solitaire Collection.

> *"Play a game of Klondike Solitaire. No solver. Win it."*

### 8. Anything you'd do yourself but don't want to do 50 times

Bulk form submissions, repetitive renaming, scheduled-time clicks,
copy-paste-massage-paste loops. The agent doesn't get bored.

---

## Build from source

Requires **.NET 10 SDK** on Windows 10 / 11.

```powershell
git clone https://github.com/ilyafainberg/TotalControl-MCP-Server.git TotalControl
cd TotalControl
dotnet build -c Release
```

### Publish as a single self-contained `.exe` (no .NET runtime needed)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\dist
```

Output: `dist\TotalControl.exe` (~140 MB self-contained, includes the .NET 10
runtime + WPF + UI Automation libraries).

For a smaller framework-dependent build (requires .NET 10 on the target
machine):

```powershell
dotnet publish -c Release -o .\dist-fd
```

### Pre-built binaries

Grab the latest zip from the
[Releases page](https://github.com/ilyafainberg/TotalControl-MCP-Server/releases),
unzip anywhere, and point your MCP host at `TotalControl.exe`.

---

## Install in Microsoft Scout

**Settings → Extensions → MCP Server → Add (stdio):**

```json
{
  "name": "totalcontrol",
  "command": "C:\\Tools\\TotalControl\\TotalControl.exe",
  "args": []
}
```

(Replace the path with wherever you unzipped the release.)

After adding, **toggle the server off then back on** so Scout re-runs
`tools/list` and picks up all 10 tools.

### Development variant (run from source)

```json
{
  "name": "totalcontrol-dev",
  "command": "dotnet",
  "args": [
    "run", "--project", "C:\\src\\TotalControl",
    "-c", "Release", "--no-build"
  ]
}
```

---

## Install in GitHub Copilot CLI

GitHub Copilot CLI ([docs](https://docs.github.com/en/copilot/github-copilot-in-the-cli))
supports MCP servers via its config file.

### One-shot from a prompt

```powershell
gh copilot mcp add totalcontrol `
  --command "C:\Tools\TotalControl\TotalControl.exe"
```

### Manual config

Edit `%USERPROFILE%\.copilot\mcp-config.json` and add a server entry:

```json
{
  "mcpServers": {
    "totalcontrol": {
      "command": "C:\\Tools\\TotalControl\\TotalControl.exe",
      "args": [],
      "env": {}
    }
  }
}
```

Restart `gh copilot` and the 10 tools become available — try:

```text
gh copilot
> open notepad and type "hello from copilot cli"
```

The CLI will surface each tool call for your approval before executing.

### Other MCP hosts

Anything that speaks MCP over stdio works — Claude Desktop, Continue,
Cline, Cursor, Goose, Zed, etc. Point the host's MCP config at
`TotalControl.exe`. The shape of the entry is always the same: a
`command` + optional `args`.

---

## Architecture

```
┌──────────────────┐   stdio JSON-RPC   ┌──────────────────────────────────┐
│  MCP Host        │ ◄─────────────────►│  TotalControl.exe                │
│  (Scout, CLI…)   │                    │  ┌────────────────────────────┐  │
└──────────────────┘                    │  │ ModelContextProtocol SDK   │  │
                                        │  │  • stdio transport         │  │
                                        │  │  • tool discovery via attrs│  │
                                        │  └─────────────┬──────────────┘  │
                                        │                │                  │
                                        │  ┌─────────────▼──────────────┐  │
                                        │  │ DesktopTools (16 tools)    │  │
                                        │  └─────────────┬──────────────┘  │
                                        │                │                  │
                                        │  ┌─────────────▼──────────────┐  │
                                        │  │ KeySequenceParser           │  │
                                        │  │ Win32 P/Invoke (SendInput,  │  │
                                        │  │ PrintWindow, EnumWindows,   │  │
                                        │  │ DwmGetWindowAttribute, …)   │  │
                                        │  │ UIAutomation                │  │
                                        │  └────────────────────────────┘  │
                                        └──────────────────────────────────┘
                                                        │
                                                        ▼
                                            Windows USER32 / GDI / DWM / UIA
```

### Files

| File | Responsibility |
|---|---|
| `Program.cs` | Hosts the MCP server over stdio, registers tools, ships agent-facing server instructions. |
| `DesktopTools.cs` | The 16 MCP tools with rich `[Description]` metadata so the agent picks them correctly. |
| `WindowControl.cs` | The "Under Agent Control" overlay frames (WPF, on a dedicated STA thread), top-most management, follow-timer, and the enforcement gate. |
| `AgentSession.cs` | User-abort state: the ✕ button cancels in-flight operations and arms a one-shot abort flag surfaced to the agent's next tool call. |
| `ScreenRecorder.cs` | Records a window to MP4 by piping DWM-aware `PrintWindow` frames (raw BGRA) to FFmpeg (H.264). |
| `KeySequenceParser.cs` | Tokenizes `{Ctrl+S}` / `{Tab 5}` / Unicode text into Win32 `INPUT` events. Chords resolve to VK codes (via `VkKeyScanW`) so hotkeys like `{Win+R}` actually fire. |
| `Win32.cs` | P/Invoke declarations: `SendInput`, `PrintWindow`, `EnumWindows`, `DwmGetWindowAttribute`, `SetWindowPos`, `GetDpiForWindow`, mouse/key flag enums, VK constants. |
| `app.manifest` | Declares Per-Monitor V2 DPI awareness — screenshots and clicks line up at physical pixels on HiDPI displays. |
| `test_*.py` | JSON-RPC stdio smoke tests for pixel accuracy, UIA lookup, keyboard/mouse, the enforcement gate, window control, and recording. |

---

## Design notes

- **DPI awareness — Per-Monitor V2.** Without it, on a 4K monitor at
  150 % scaling, screenshots come back at logical pixels and click
  coordinates land in the wrong place. The `app.manifest` opts into PMv2
  so the pixels you see *are* the pixels you click.
- **Window-relative coordinates.** `click_in_window` and `screenshot_window`
  use the same window-origin math (`DwmGetWindowAttribute` →
  `GetWindowRect` fallback), so `(imageX, imageY)` from a `screenshot_window`
  PNG can be passed straight to `click_in_window`. No arithmetic on the
  agent side, no DPI surprises.
- **Unicode-first keyboard.** `send_keys` uses `KEYEVENTF_UNICODE` for
  text so it works with any IME, any keyboard layout, any character — no
  AZERTY / QWERTZ guessing, no Shift-state bugs.
- **PrintWindow with `PW_RENDERFULLCONTENT`.** Plain `BitBlt` fails on
  Chromium / UWP / WebView2 surfaces because they composite via DWM.
  The full-content flag captures them correctly. Falls back to desktop
  copy for DirectX / protected surfaces that refuse `PrintWindow`.
- **UI Automation as a click delivery channel.** Synthetic `SendInput`
  clicks are silently dropped by some UWP / AppContainer apps (Microsoft
  Solitaire Collection is the canonical example). `find_element invoke=true`
  goes through the accessibility layer instead, which those apps cannot
  ignore.
- **Logs to stderr.** MCP uses stdout for the JSON-RPC channel — any
  stray write would break the protocol.
  `Microsoft.Extensions.Logging` is wired to stderr.

---

## Disclaimer (repeated, on purpose)

This software is provided **"AS IS", WITHOUT WARRANTY OF ANY KIND**,
express or implied, including but not limited to the warranties of
merchantability, fitness for a particular purpose, and non-infringement.
**In no event shall the author or copyright holder be liable for any
claim, damages, or other liability**, whether in an action of contract,
tort, or otherwise, arising from, out of, or in connection with the
software or the use or other dealings in the software. See
[LICENSE](LICENSE) for the full MIT license text.

**This is not a Microsoft product.** It is a personal open-source
project. Do not file Microsoft support tickets about it. Use it at your
own risk on machines you can afford to lose.

---

## Contributing

Issues and pull requests welcome at
<https://github.com/ilyafainberg/TotalControl-MCP-Server>. Ideas:

- `scroll_mouse` (vertical / horizontal wheel)
- `find_window` (returns the list of candidate windows without capturing)
- `crop_screenshot` (region-of-interest capture to reduce token cost)
- Lone-modifier tokens in `send_keys` (`{Win}` standalone)
- macOS / Linux ports (would need a different P/Invoke surface)

---

## Credits

Built by **Ilya Fainberg**. Released under the MIT License.

Powered by the official
[Model Context Protocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk),
jointly maintained by Microsoft and Anthropic.
