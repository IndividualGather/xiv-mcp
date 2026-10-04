using System;

namespace XivMcp.Fishing;

/// <summary>What the game looks like at one check of a fishing run.</summary>
public sealed record FishingState(DateTimeOffset Now, int Caught, bool Fishing, bool CanCast, int BaitLeft, int FreeSlots);

public enum FishingAction { Continue, Cast, Done, Stop }

/// <summary>What to do next, and for <see cref="FishingAction.Stop"/> why, in words for the player.</summary>
public sealed record FishingDecision(FishingAction Action, string? Reason = null);

/// <summary>
/// The decisions of fishing until a number of fish are caught, with AutoHook hooking and casting again: when to wait for the
/// fish's window, when to cast, and when to stop. The plugin only reads the game and acts on the answer.
/// </summary>
public sealed class FishingRun
{
    /// <summary>How long nothing may happen (no line in the water) before XIV MCP casts itself.</summary>
    public static readonly TimeSpan IdleBeforeCast = TimeSpan.FromSeconds(6);

    private readonly FishGuideNames names;
    private readonly int wanted;
    private readonly DateTimeOffset started;
    private readonly TimeSpan timeout;
    private readonly bool stopWithWindow;
    private readonly Func<DateTimeOffset, FishWindow?> nextWindow;
    private DateTimeOffset idleSince;

    /// <param name="nextWindow">The fish's window open at a moment or the next one, or null for a fish that bites any time.</param>
    public FishingRun(FishGuideNames names, int wanted, DateTimeOffset started, TimeSpan timeout, bool stopWithWindow,
                      Func<DateTimeOffset, FishWindow?> nextWindow)
    {
        this.names = names;
        this.wanted = wanted;
        this.started = started;
        this.timeout = timeout;
        this.stopWithWindow = stopWithWindow;
        this.nextWindow = nextWindow;
        idleSince = started;
    }

    /// <summary>How long to wait before fishing, for a window that is not open yet; <paramref name="reason"/> says why not to when it opens too late.</summary>
    public TimeSpan WaitBeforeFishing(DateTimeOffset now, out string? reason)
    {
        reason = null;
        var window = nextWindow(now);
        if (window is null || window.Start <= now) return TimeSpan.Zero;
        if (window.Start - started > timeout)
        {
            reason = $"The next window for {names.Fish} opens at {window.Start.ToLocalTime():HH:mm}, after the timeout.";
            return TimeSpan.Zero;
        }
        return window.Start - now;
    }

    public FishingDecision Next(FishingState s)
    {
        if (s.Caught >= wanted) return new FishingDecision(FishingAction.Done);
        var progress = $"caught {s.Caught} of {wanted} {names.Fish}";
        if (s.FreeSlots == 0) return new FishingDecision(FishingAction.Stop, $"Bags are full; {progress}.");
        if (s.BaitLeft == 0 && !s.Fishing) return new FishingDecision(FishingAction.Stop, $"Out of {names.Bait}; {progress}.");
        if (s.Now - started > timeout) return new FishingDecision(FishingAction.Stop, $"Timed out; {progress}.");
        if (stopWithWindow && nextWindow(s.Now) is { } w && w.Start > s.Now)
            return new FishingDecision(FishingAction.Stop, $"The window closed; {progress}. The next one opens at {w.Start.ToLocalTime():HH:mm}.");

        // AutoHook casts again after each catch; XIV MCP casts when nothing has happened for a while (the first cast, or after a pause).
        if (s.Fishing)
        {
            idleSince = s.Now;
            return new FishingDecision(FishingAction.Continue);
        }
        if (s.Now - idleSince <= IdleBeforeCast) return new FishingDecision(FishingAction.Continue);
        if (!s.CanCast) return new FishingDecision(FishingAction.Stop, $"Cannot cast here. Stand at the water of {names.Spot} and face it.");
        idleSince = s.Now;
        return new FishingDecision(FishingAction.Cast);
    }
}

/// <summary>Names for the messages of a fishing run.</summary>
public sealed record FishGuideNames(string Fish, string Bait, string Spot);
