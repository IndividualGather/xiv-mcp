using XivMcp.Gear;

namespace XivMcp.Tests;

public class GearSettleTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);
    private static GearState Free(string gear = "a,b,c") => new(JobMatches: true, Busy: false, Equipped: gear);

    [Fact]
    public void Settled_once_the_gear_stays_unchanged_for_the_quiet_time()
    {
        var settle = new GearSettle(Quiet);
        Assert.False(settle.Observe(T0, Free()));
        Assert.False(settle.Observe(T0.AddMilliseconds(500), Free()));
        Assert.True(settle.Observe(T0.AddMilliseconds(1000), Free()));
    }

    [Fact]
    public void Gear_still_changing_is_not_settled()
    {
        var settle = new GearSettle(Quiet);
        settle.Observe(T0, Free("a"));
        Assert.False(settle.Observe(T0.AddMilliseconds(900), Free("a,b")));
        Assert.False(settle.Observe(T0.AddMilliseconds(1500), Free("a,b")));
        Assert.True(settle.Observe(T0.AddMilliseconds(1900), Free("a,b")));
    }

    [Fact]
    public void Busy_or_wrong_job_is_not_settled_and_restarts_the_quiet_time()
    {
        var settle = new GearSettle(Quiet);
        settle.Observe(T0, Free());
        Assert.False(settle.Observe(T0.AddSeconds(2), Free() with { Busy = true }));
        Assert.False(settle.Observe(T0.AddSeconds(3), Free() with { JobMatches = false }));
        Assert.False(settle.Observe(T0.AddSeconds(3.5), Free()));
        Assert.True(settle.Observe(T0.AddSeconds(4.5), Free()));
    }

    [Theory]
    [InlineData(1, true, EquipStep.Done)]
    [InlineData(1, false, EquipStep.Retry)]
    [InlineData(2, false, EquipStep.Retry)]
    [InlineData(3, false, EquipStep.GiveUp)]
    [InlineData(3, true, EquipStep.Done)]
    public void A_refused_item_is_tried_again_twice(int attempt, bool worn, EquipStep expected) =>
        Assert.Equal(expected, EquipRetry.Next(attempt, worn));

    [Fact]
    public void Refused_message_says_how_often_it_was_tried()
    {
        Assert.Equal("Snow Cotton Beret was not put on after 3 tries (the game refused it; is the character busy, e.g. still changing job?).",
                     EquipRetry.Refused("Snow Cotton Beret", 3));
    }
}
