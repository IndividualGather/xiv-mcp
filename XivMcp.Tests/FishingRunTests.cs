using System;
using XivMcp.Fishing;

namespace XivMcp.Tests;

public class FishingRunTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1759600000);
    private static readonly FishGuideNames Names = new("Big One", "Bait Bug", "The Lake");

    private static FishingRun Run(FishWindow? window = null, int wanted = 2, bool stopWithWindow = true) =>
        new(Names, wanted, T0, TimeSpan.FromMinutes(30), stopWithWindow, now => window is null ? null : window.End > now ? window : new FishWindow(window.Start.AddHours(1), window.End.AddHours(1)));

    private static FishingState At(int seconds, int caught = 0, bool fishing = false, bool canCast = true, int bait = 10, int free = 5) =>
        new(T0.AddSeconds(seconds), caught, fishing, canCast, bait, free);

    [Fact]
    public void Casts_when_nothing_happens_and_waits_while_fishing()
    {
        var run = Run();
        Assert.Equal(FishingAction.Continue, run.Next(At(2)).Action);
        Assert.Equal(FishingAction.Cast, run.Next(At(7)).Action);
        Assert.Equal(FishingAction.Continue, run.Next(At(9)).Action); // just cast
        Assert.Equal(FishingAction.Continue, run.Next(At(20, fishing: true)).Action);
        Assert.Equal(FishingAction.Continue, run.Next(At(24)).Action); // AutoHook is about to cast again
        Assert.Equal(FishingAction.Cast, run.Next(At(27)).Action);
    }

    [Fact]
    public void Done_when_enough_are_caught()
    {
        Assert.Equal(FishingAction.Done, Run().Next(At(60, caught: 2)).Action);
    }

    [Theory]
    [InlineData(0, 10, 2000, "Bags are full; caught 1 of 2 Big One.")]
    [InlineData(5, 0, 60, "Out of Bait Bug; caught 1 of 2 Big One.")]
    [InlineData(5, 10, 1801, "Timed out; caught 1 of 2 Big One.")]
    public void Stops_with_a_reason(int free, int bait, int seconds, string reason)
    {
        var d = Run().Next(At(seconds, caught: 1, bait: bait, free: free));
        Assert.Equal(FishingAction.Stop, d.Action);
        Assert.Equal(reason, d.Reason);
    }

    [Fact]
    public void Stops_when_the_window_closes_unless_told_otherwise()
    {
        var window = new FishWindow(T0, T0.AddMinutes(5));
        Assert.Equal(FishingAction.Continue, Run(window).Next(At(60, fishing: true)).Action);
        Assert.Equal(FishingAction.Stop, Run(window).Next(At(400, fishing: true)).Action);
        Assert.Equal(FishingAction.Continue, Run(window, stopWithWindow: false).Next(At(400, fishing: true)).Action);
    }

    [Fact]
    public void Cannot_cast_away_from_the_water()
    {
        var d = Run().Next(At(10, canCast: false));
        Assert.Equal(FishingAction.Stop, d.Action);
        Assert.Contains("The Lake", d.Reason);
    }

    [Fact]
    public void Waits_for_a_window_that_opens_soon_but_not_for_one_after_the_timeout()
    {
        var soon = new FishingRun(Names, 1, T0, TimeSpan.FromMinutes(30), true, _ => new FishWindow(T0.AddMinutes(10), T0.AddMinutes(15)));
        Assert.Equal(TimeSpan.FromMinutes(10), soon.WaitBeforeFishing(T0, out var none));
        Assert.Null(none);

        var late = new FishingRun(Names, 1, T0, TimeSpan.FromMinutes(30), true, _ => new FishWindow(T0.AddMinutes(45), T0.AddMinutes(50)));
        late.WaitBeforeFishing(T0, out var reason);
        Assert.Contains("after the timeout", reason);

        Assert.Equal(TimeSpan.Zero, Run().WaitBeforeFishing(T0, out _));
    }
}
