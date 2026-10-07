using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Util;

/// <summary>
/// Asks the player in game before something happens that the assistant (or a third-party plugin) should not decide alone: a purchase
/// paid with tomestones, a recalled venture, a plugin tool that wants to spend gil. The request shows in a popup window; the call waits
/// until the player answers. Only the player's click can approve — there is no tool argument for it.
/// </summary>
internal static class Consent
{
    public sealed class Request(string title, IReadOnlyList<string> details)
    {
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>When it was asked: the approve buttons only work after a moment, so a click meant for the game can't approve it.</summary>
        public DateTime AskedUtc { get; } = DateTime.UtcNow;
        public string Title { get; } = title;
        public IReadOnlyList<string> Details { get; } = details;
        public DateTime Asked { get; } = DateTime.UtcNow;
        public DateTime Deadline { get; init; }

        /// <summary>Who asks: "your AI assistant (client)", or a plugin by name.</summary>
        public string Source { get; init; } = "your AI assistant";

        /// <summary>Shown highlighted, e.g. "Can't be undone."</summary>
        public string? Warning { get; init; }

        public RiskLevel Risk { get; init; } = RiskLevel.Medium;

        /// <summary>Offer "Approve for this session" (never for critical capabilities).</summary>
        public bool OfferSession { get; init; }

        /// <summary>Offer "Always allow" (sets the capabilities to Allow for that plugin; critical ones excluded).</summary>
        public bool OfferAlways { get; init; }

        internal TaskCompletionSource<ApprovalDecision> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly List<Request> Pending = [];
    private static readonly Lock Sync = new();

    /// <summary>Raised when a new request arrives (the window opens itself).</summary>
    public static event Action? Asked;

    public static IReadOnlyList<Request> Open
    {
        get { lock (Sync) return Pending.ToList(); }
    }

    /// <summary>Shows the request and waits for the answer; no answer within <paramref name="timeout"/> counts as declined.</summary>
    public static async Task<ApprovalDecision> Ask(Request request, TimeSpan timeout, CancellationToken ct)
    {
        lock (Sync) Pending.Add(request);
        Svc.Log.Information($"[MCP] Asking the player ({request.Source}): {request.Title}");
        Asked?.Invoke();
        Taskbar.Flash(); // the player may be in another window while the game waits for them
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { return await request.Answer.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ApprovalDecision.Denied; }
        }
        finally
        {
            lock (Sync) Pending.Remove(request);
        }
    }

    /// <summary>Shows the request in game and waits for the answer. Throws if the player declines or does not answer in time.</summary>
    public static async Task Require(string title, IReadOnlyList<string> details, TimeSpan timeout, CancellationToken ct)
    {
        var request = new Request(title, details) { Deadline = DateTime.UtcNow + timeout };
        var answer = await Ask(request, timeout, ct).ConfigureAwait(false);
        if (answer != ApprovalDecision.Denied) return;
        throw new ToolException(DateTime.UtcNow >= request.Deadline
            ? $"No answer in game within {timeout.TotalSeconds:0} s; nothing was done. Ask the player to watch for the XIV MCP approval window."
            : "The player declined in game; nothing was done.");
    }

    public static void Answer(Request request, ApprovalDecision decision)
    {
        Svc.Log.Information($"[MCP] Player answered {decision}: {request.Title}");
        request.Answer.TrySetResult(decision);
    }

    public static void Answer(Request request, bool approved) => Answer(request, approved ? ApprovalDecision.ApprovedOnce : ApprovalDecision.Denied);

    public static void DeclineAll()
    {
        foreach (var r in Open) r.Answer.TrySetResult(ApprovalDecision.Denied);
    }
}
