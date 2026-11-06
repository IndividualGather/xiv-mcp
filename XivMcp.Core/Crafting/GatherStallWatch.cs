using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Crafting;

/// <summary>
/// What the character is doing while GatherBuddy auto-gathers. <see cref="Busy"/>: gathering, fishing or spearfishing, mounted or
/// flying, walking a path, casting (teleport) or loading a zone. <see cref="Waiting"/>: GatherBuddy waits for a timed node.
/// </summary>
public readonly record struct GatherActivity(bool Busy, bool Waiting, float X, float Y, float Z);

/// <summary>
/// Notices GatherBuddy hanging: auto-gather stays on, but no target count changes and the character just stands there (not
/// gathering, fishing, travelling or waiting for a timed node) for a whole window.
/// </summary>
public sealed class GatherStallWatch
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(6);

    /// <summary>Moving less than this (yalms) between two looks is standing still.</summary>
    public const float MoveThreshold = 1f;

    private readonly TimeSpan window;
    private DateTime lastActivity;
    private Dictionary<uint, int>? lastCounts;
    private (float X, float Y, float Z)? lastPosition;

    /// <param name="window">How long idle without progress counts as stuck; zero or less turns the check off.</param>
    public GatherStallWatch(TimeSpan window, DateTime now)
    {
        this.window = window;
        lastActivity = now;
    }

    /// <summary>Records a look at the game; true once the character has been idle without progress for the whole window.</summary>
    public bool Observe(DateTime now, IReadOnlyDictionary<uint, int> counts, GatherActivity activity)
    {
        var changed = lastCounts is null || lastCounts.Count != counts.Count
                      || counts.Any(c => !lastCounts.TryGetValue(c.Key, out var before) || before != c.Value);
        var moved = lastPosition is { } p && Distance(p, (activity.X, activity.Y, activity.Z)) >= MoveThreshold;
        // The first look only sets the baseline.
        if ((changed && lastCounts is not null) || moved || activity.Busy || activity.Waiting) lastActivity = now;
        lastCounts = counts.ToDictionary(c => c.Key, c => c.Value);
        lastPosition = (activity.X, activity.Y, activity.Z);
        return window > TimeSpan.Zero && now - lastActivity >= window;
    }

    /// <summary>Starts the idle clock again, e.g. after GatherBuddy was started again.</summary>
    public void Reset(DateTime now) => lastActivity = now;

    public TimeSpan IdleFor(DateTime now) => now - lastActivity;

    /// <summary>GatherBuddy waits for a timed node: its IPC says so, or its status text does.</summary>
    public static bool IsWaiting(bool? ipcWaiting, string? status) =>
        ipcWaiting == true || (status?.Contains("waiting", StringComparison.OrdinalIgnoreCase) ?? false);

    public static string Message(IEnumerable<string> missing, string? status, TimeSpan idle) =>
        $"GatherBuddy seems stuck: the character has stood idle for {(int)idle.TotalMinutes} minutes and no count changed " +
        $"({(string.IsNullOrWhiteSpace(status) ? "GatherBuddy shows no status" : $"GatherBuddy's status: \"{status}\"")}). " +
        $"Still missing: {string.Join(", ", missing)}. Skip these items or get them separately (e.g. fish them with fish_until, " +
        "buy them, or gather them by hand), then run gather_until again without them.";

    private static float Distance((float X, float Y, float Z) a, (float X, float Y, float Z) b)
    {
        var (dx, dy, dz) = (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
