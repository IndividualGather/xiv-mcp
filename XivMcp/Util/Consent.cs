using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Asks the player in game before something is spent that the assistant should not decide alone (e.g. a purchase paid with
/// tomestones, scrips or seals). The request shows in a popup window; the tool call waits until the player approves or declines.
/// Only the player's click can approve — there is no tool argument for it.
/// </summary>
internal static class Consent
{
    public sealed class Request(string title, IReadOnlyList<string> details)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; } = title;
        public IReadOnlyList<string> Details { get; } = details;
        public DateTime Asked { get; } = DateTime.UtcNow;
        public DateTime Deadline { get; init; }
        internal TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly List<Request> Pending = [];
    private static readonly Lock Sync = new();

    /// <summary>Raised when a new request arrives (the window opens itself).</summary>
    public static event Action? Asked;

    public static IReadOnlyList<Request> Open
    {
        get { lock (Sync) return Pending.ToList(); }
    }

    /// <summary>Shows the request in game and waits for the answer. Throws if the player declines or does not answer in time.</summary>
    public static async Task Require(string title, IReadOnlyList<string> details, TimeSpan timeout, CancellationToken ct)
    {
        var request = new Request(title, details) { Deadline = DateTime.UtcNow + timeout };
        lock (Sync) Pending.Add(request);
        Svc.Log.Information($"[MCP] Asking the player: {title}");
        Asked?.Invoke();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            bool approved;
            try { approved = await request.Answer.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ToolException($"No answer in game within {timeout.TotalSeconds:0} s; nothing was done. Ask the player to watch for the XIV MCP approval window.");
            }
            if (!approved) throw new ToolException("The player declined in game; nothing was done.");
        }
        finally
        {
            lock (Sync) Pending.Remove(request);
        }
    }

    public static void Answer(Request request, bool approved)
    {
        Svc.Log.Information($"[MCP] Player {(approved ? "approved" : "declined")}: {request.Title}");
        request.Answer.TrySetResult(approved);
    }

    public static void DeclineAll()
    {
        foreach (var r in Open) r.Answer.TrySetResult(false);
    }
}
