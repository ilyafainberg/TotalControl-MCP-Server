// -----------------------------------------------------------------------------
//  TotalControl — Program entry point and MCP host wiring
//
//  Licensed under the MIT License. See LICENSE in the project root for details.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace TotalControl;

/// <summary>
/// Boots the MCP server over stdio, registers every [McpServerTool] in this
/// assembly, and ships a ServerInstructions block that teaches the agent how
/// to drive the desktop responsibly.
/// </summary>
internal sealed class Program
{
    private const string ServerInstructions = """
        TotalControl gives you hardware-level control of the user's Windows desktop: synthesized mouse
        movement and clicks, keyboard input, full-screen capture, and per-window capture. Use it to drive
        ANY application that has no API — native Win32, WPF, UWP, WinForms, Electron/Chromium, WebView2,
        legacy LOB apps, installer wizards, configuration dialogs, virtual machine consoles — anything you
        can see on screen, you can control.

        MANDATORY WINDOW SELECTION (enforced by the server):
        Before you can screenshot, click, type, drag, scroll, or inspect anything, you MUST select the
        target window. Capture and interaction tools are GATED — if no window is selected they return an
        instruction instead of acting. The required workflow is:
          1. find_window(filter?)   — list open windows as TEXT (no screenshot needed) to discover targets.
          2. control_window(query)  — SELECT + pin the window (it goes top-most, a crimson 'Under Agent
                                      Control' frame appears so the user sees what you are driving).
          3. screenshot_window / click_in_window / send_keys / … — now permitted on that window.
          4. release_window(query) or release_window(all=true) — REQUIRED when finished, to un-pin the
                                      window and remove the frame. Always release before ending the task.
        screenshot_screen and absolute mouse/keyboard tools require at least one controlled window;
        window-specific tools (screenshot_window, click_in_window, find_element, crop_screenshot) require
        THAT specific window to be controlled. Do not fight the gate — follow the instruction it returns.

        USER ABORT: the user can STOP you at any time by clicking the ✕ on the crimson 'Under Agent Control'
        frame. When that happens, any running operation (e.g. a recording) is cancelled and your NEXT tool
        call returns an '⛔ ABORTED BY USER' message. If you see it, STOP the current task immediately, do not
        re-acquire the window, and ask the user how to proceed.

        RECORDING: record_window captures a controlled window to an MP4 for a fixed duration (needs FFmpeg).
        It blocks for the duration and is abortable via the ✕ button.

        STANDARD LOOP (always follow this pattern once a window is selected):
          1. screenshot_window for the controlled app to SEE current state.
          2. Decide what to click, where to move, what to type.
          3. Act with click_in_window / send_keys (or move_mouse / click_mouse).
          4. screenshot again to VERIFY the action had the intended effect before continuing.
        Skipping the verification screenshot is the #1 cause of cascading automation failure — the screen
        may have changed in ways you did not predict (popup, focus shift, slow UI response).

        COORDINATES are physical screen pixels. The server is PerMonitor-V2 DPI-aware, so the pixels you
        see in a screenshot ARE the pixels you pass to move_mouse / click_mouse. Origin (0,0) is the
        top-left of the primary monitor. On multi-monitor setups, secondary displays can have negative
        coordinates.

        FOCUS: send_keys types into whichever window currently has keyboard focus. Always click the target
        control (or use {Alt+Tab}/{Win}) before typing into a new window.

        WORKING ON A SPECIFIC WINDOW: agents often mis-click because the target window is in the background
        and something else is on top at those coordinates. Before driving a specific app, call
        control_window(query) — it pins that window top-most, brings it forward, and draws a crimson
        'Under Agent Control' frame so the user can see what you are doing. Do your screenshots/clicks/typing,
        then YOU MUST call release_window(query) (or release_window(all=true)) when finished — otherwise the
        window stays top-most permanently. Always release every window you controlled before ending a task.

        TIMING: real desktop apps need time to render and respond. After clicking a button that opens a
        dialog, expect ~200–800 ms before the dialog is fully drawn — take a verification screenshot
        before acting on it. If the screenshot shows a half-rendered UI, screenshot again.

        SAFETY: these tools manipulate the user's REAL desktop. There is no sandbox. Do not click
        unfamiliar buttons, do not type into windows you cannot identify, and never invoke shutdown,
        format, delete, or 'send' actions without explicit user confirmation. Prefer reading (screenshot)
        over writing (click/type) when uncertain.
        """;

    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // MCP transports stdout for the JSON-RPC protocol. Anything we write
        // to stdout that isn't a valid JSON-RPC frame breaks the protocol, so
        // every logger MUST emit to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // Register the server with the MCP SDK:
        //   • AddMcpServer + ServerInfo / ServerInstructions    — identity + agent prompt
        //   • WithStdioServerTransport                          — bidirectional stdin/stdout JSON-RPC
        //   • WithToolsFromAssembly                             — discovers [McpServerToolType] classes
        //                                                         and their [McpServerTool] methods via reflection
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "TotalControl", Version = "1.6.0" };
                o.ServerInstructions = ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}
