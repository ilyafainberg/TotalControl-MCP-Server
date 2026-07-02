// -----------------------------------------------------------------------------
//  TotalControl — Agent session state (user-abort + cancellable operations)
//
//  Licensed under the MIT License. See LICENSE in the project root for details.
//
//  Author: Ilya Fainberg <ifain@microsoft.com>
//
//  Tracks a single piece of cross-tool state: whether the USER has aborted the
//  agent by clicking the ✕ on an "Under Agent Control" frame. When that happens
//  we (a) cancel any in-flight long-running operation (e.g. a screen recording)
//  and (b) raise a one-shot "aborted" flag that the next capture / interaction
//  tool surfaces to the agent, instructing it to STOP and check with the user.
// -----------------------------------------------------------------------------

namespace TotalControl;

internal static class AgentSession
{
    private static readonly object Gate = new();
    private static bool aborted;
    private static string? abortMessage;
    private static readonly HashSet<CancellationTokenSource> LiveOperations = new();

    /// <summary>
    /// The user closed a control frame (✕). Cancel every registered long-running
    /// operation and arm the one-shot abort flag for the next tool call.
    /// </summary>
    public static void SignalUserAbort(string title)
    {
        lock (Gate)
        {
            aborted = true;
            abortMessage =
                $"⛔ ABORTED BY USER — the user clicked ✕ on the 'Under Agent Control' frame of '{title}'.\n" +
                "This is an explicit STOP signal. Do NOT continue the current task and do NOT re-acquire the\n" +
                "window automatically. Any in-progress recording or operation has been cancelled. Stop now,\n" +
                "tell the user you have stopped, and ask how they want to proceed.";
            foreach (var cts in LiveOperations)
            {
                try { cts.Cancel(); } catch { /* already disposed */ }
            }
        }
    }

    /// <summary>
    /// If a user-abort is pending, return its message and clear it (consume-once).
    /// Otherwise null. Tools call this at entry so the abort surfaces exactly once.
    /// </summary>
    public static string? ConsumeAbort()
    {
        lock (Gate)
        {
            if (!aborted) return null;
            aborted = false;
            var m = abortMessage;
            abortMessage = null;
            return m;
        }
    }

    /// <summary>
    /// Register a cancellation source for a long-running operation so a user-abort
    /// can cancel it. Dispose the returned handle when the operation finishes.
    /// </summary>
    public static IDisposable RegisterOperation(CancellationTokenSource cts)
    {
        lock (Gate) LiveOperations.Add(cts);
        return new Registration(cts);
    }

    private sealed class Registration : IDisposable
    {
        private readonly CancellationTokenSource cts;
        public Registration(CancellationTokenSource c) => cts = c;
        public void Dispose() { lock (Gate) LiveOperations.Remove(cts); }
    }
}
