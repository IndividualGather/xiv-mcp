using XivMcp.CustomDeliveries;

namespace XivMcp.Tests;

public class TurnInWatchTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Questionable_is_turning_in_only_once_everything_is_gathered()
    {
        // While it still gathers, items in the bags don't mean it is at the client.
        Assert.False(TurnInWatch.QuestionableTurningIn(questionableRunning: true, inBags: 2, needed: 3));
        Assert.True(TurnInWatch.QuestionableTurningIn(questionableRunning: true, inBags: 3, needed: 3));
        Assert.False(TurnInWatch.QuestionableTurningIn(questionableRunning: false, inBags: 3, needed: 3));
    }

    [Fact]
    public void Gathered_items_are_turned_in_by_questionable_not_satisfier()
    {
        // Satisfier's own turn-in (straight to the game's agent) fails for some clients (Tiisol Ja); Questionable's works.
        Assert.True(TurnInWatch.TurnInWithQuestionable(isGather: true, inBags: 1, toDeliver: 1));
        Assert.False(TurnInWatch.TurnInWithQuestionable(isGather: true, inBags: 0, toDeliver: 3)); // Satisfier has Questionable gather
        Assert.False(TurnInWatch.TurnInWithQuestionable(isGather: false, inBags: 3, toDeliver: 3));
    }

    [Fact]
    public void Gathering_never_counts_as_a_stalled_turn_in()
    {
        var watch = new TurnInWatch(TimeSpan.FromSeconds(45));
        Assert.False(watch.Stalled(turningIn: false, lastProgress: T0, now: T0.AddMinutes(5)));
    }

    [Fact]
    public void The_stall_clock_starts_when_turning_in_starts_not_when_the_delivery_did()
    {
        // Gathering took minutes; turning in has only just begun.
        var watch = new TurnInWatch(TimeSpan.FromSeconds(45));
        Assert.False(watch.Stalled(turningIn: true, lastProgress: T0, now: T0.AddMinutes(4)));
        Assert.False(watch.Stalled(turningIn: true, lastProgress: T0, now: T0.AddMinutes(4).AddSeconds(30)));
        Assert.True(watch.Stalled(turningIn: true, lastProgress: T0, now: T0.AddMinutes(4).AddSeconds(46)));
    }

    [Fact]
    public void A_delivery_that_went_in_restarts_the_clock()
    {
        var watch = new TurnInWatch(TimeSpan.FromSeconds(45));
        Assert.False(watch.Stalled(true, T0, T0));
        var delivered = T0.AddSeconds(40);
        Assert.False(watch.Stalled(true, delivered, T0.AddSeconds(80)));
        Assert.True(watch.Stalled(true, delivered, T0.AddSeconds(86)));
    }

    [Fact]
    public void Stopping_to_turn_in_resets_the_clock()
    {
        var watch = new TurnInWatch(TimeSpan.FromSeconds(45));
        Assert.False(watch.Stalled(true, T0, T0));
        Assert.False(watch.Stalled(false, T0, T0.AddSeconds(30))); // back to gathering, say
        Assert.False(watch.Stalled(true, T0, T0.AddSeconds(60)));
        Assert.True(watch.Stalled(true, T0, T0.AddSeconds(106)));
    }
}
