using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Ui;

/// <summary>A tool call the server is running (or ran a moment ago), for the overlay. <see cref="Ends"/>: when a waiting call is due to end.</summary>
public sealed record CallActivity(long Id, string Tool, string? Client, DateTime Started, DateTime? Finished, bool Failed, DateTime? Ends = null)
{
    public bool Running => Finished is null;
}

/// <summary>
/// The tool calls in flight on the MCP server, so the overlay can show them. Quick calls (most reads) are kept out of
/// <see cref="Visible"/> by a delay, so the overlay doesn't flicker; finished calls stay visible for a moment.
/// </summary>
public sealed class ActivityTracker
{
    private readonly object sync = new();
    private readonly List<CallActivity> calls = [];
    private long nextId;

    /// <summary>True while any call is in flight, however short.</summary>
    public bool AnyRunning
    {
        get { lock (sync) return calls.Any(c => c.Running); }
    }

    public long Begin(string tool, string? client, DateTime now, DateTime? ends = null)
    {
        lock (sync)
        {
            var id = ++nextId;
            calls.Add(new CallActivity(id, tool, client, now, null, false, ends));
            return id;
        }
    }

    public void End(long id, bool failed, DateTime now)
    {
        lock (sync)
        {
            var i = calls.FindIndex(c => c.Id == id);
            if (i >= 0) calls[i] = calls[i] with { Finished = now, Failed = failed };
            // Forget calls that can't show any more.
            calls.RemoveAll(c => c.Finished is { } f && now - f > TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>
    /// Calls to show: running ones that have run at least <paramref name="showAfter"/>, and finished ones that ran that long and ended
    /// within <paramref name="linger"/>. Running first, then newest first.
    /// </summary>
    public IReadOnlyList<CallActivity> Visible(DateTime now, TimeSpan showAfter, TimeSpan linger)
    {
        lock (sync)
            return calls.Where(c => c.Finished is { } f ? f - c.Started >= showAfter && now - f <= linger : now - c.Started >= showAfter)
                        .OrderByDescending(c => c.Running)
                        .ThenByDescending(c => c.Started)
                        .ToList();
    }
}
