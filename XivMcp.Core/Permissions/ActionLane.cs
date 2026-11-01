using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;

namespace XivMcp.Permissions;

/// <summary>
/// Lets one call that acts in the game run at a time. Two at once drive the same game windows against each other, which can leave the
/// game holding the character with no window left (a deadlocked client). A second call from the assistant is refused right away and
/// names the running one; a job step waits for its turn. Reading, stopping, job control, waiting and XIV MCP's own windows never queue.
/// </summary>
public sealed class ActionLane(Func<DateTime> now)
{
    /// <summary>Tools that change something but never drive the game's windows, or must get through to stop what runs.</summary>
    public static readonly IReadOnlySet<string> Exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Job control
        "start_job", "list_jobs", "get_job", "update_job", "pause_job", "resume_job", "cancel_job",
        // Stopping what runs
        "stop_navigation", "stop_fishing", "stop_questing", "stop_saucy", "stop_custom_delivery", "stop_duty", "cancel_trade",
        // Only waiting
        "wait", "wait_until_arrived", "wait_for_cache_refresh",
        // XIV MCP's own windows and approvals
        "request_spending_approval", "revoke_approval", "show_xivmcp_window", "press_xivmcp_control",
    };

    private readonly SemaphoreSlim slot = new(1, 1);
    private string? running;
    private DateTime since;

    public static bool Needs(McpTool tool) => !tool.ReadOnly && !Exempt.Contains(tool.Name);

    /// <summary>The tool holding the lane, if any.</summary>
    public string? Running => running;

    /// <summary>Takes the lane for <paramref name="tool"/>: waits for it when <paramref name="wait"/>, otherwise refuses if it is taken.</summary>
    public async Task<IDisposable> EnterAsync(string tool, bool wait, CancellationToken ct)
    {
        if (wait) await slot.WaitAsync(ct).ConfigureAwait(false);
        else if (!slot.Wait(0))
        {
            var holder = running ?? "Another tool";
            var seconds = Math.Max(0, (int)(now() - since).TotalSeconds);
            throw new ToolException($"{holder} is still running (for {seconds} s). Tools that act in the game run one at a time, so they can't get in " +
                                    $"each other's way: call {tool} again when it has finished.");
        }
        running = tool;
        since = now();
        return new Turn(this);
    }

    private sealed class Turn(ActionLane lane) : IDisposable
    {
        private int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 1) return;
            lane.running = null;
            lane.slot.Release();
        }
    }
}
