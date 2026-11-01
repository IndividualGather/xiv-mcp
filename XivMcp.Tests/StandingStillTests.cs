using XivMcp.Maps;

namespace XivMcp.Tests;

public class StandingStillTests
{
    [Fact]
    public void A_player_who_just_arrived_is_not_standing_still_yet()
    {
        var still = new StandingStill(samples: 3);
        Assert.False(still.Add(10, 0, 10));
        Assert.False(still.Add(10, 0, 10));
    }

    [Fact]
    public void The_same_spot_for_enough_samples_is_standing_still()
    {
        var still = new StandingStill(samples: 3);
        still.Add(10, 0, 10);
        still.Add(10, 0, 10);
        Assert.True(still.Add(10.01f, 0, 10));
    }

    [Fact]
    public void Moving_starts_the_count_again()
    {
        var still = new StandingStill(samples: 3);
        still.Add(10, 0, 10);
        still.Add(10, 0, 10);
        Assert.False(still.Add(10.5f, 0, 10));
        Assert.False(still.Add(10.5f, 0, 10));
        Assert.True(still.Add(10.5f, 0, 10));
    }

    [Fact]
    public void Still_while_mounted_or_casting_does_not_count()
    {
        var still = new StandingStill(samples: 2);
        still.Add(10, 0, 10);
        Assert.False(still.Add(10, 0, 10, busy: true));
        Assert.False(still.Add(10, 0, 10));
        Assert.True(still.Add(10, 0, 10));
    }
}
