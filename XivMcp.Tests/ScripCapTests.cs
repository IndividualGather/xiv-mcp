using XivMcp.CustomDeliveries;

namespace XivMcp.Tests;

public class ScripCapTests
{
    private static CappedReward Orange(long have, int per) => new("Orange Gatherers' Scrip", have, 4000, per);
    private static CappedReward Purple(long have, int per) => new("Purple Gatherers' Scrip", have, 4000, per);

    [Fact]
    public void Everything_fits_with_room_to_spare()
    {
        Assert.Equal(3, ScripCap.DeliveriesThatFit([Orange(1000, 116), Purple(1000, 140)], 3));
    }

    [Fact]
    public void Deliveries_stop_before_a_currency_would_go_over_its_cap()
    {
        // 3,881 of 4,000: room for 119, one delivery of up to 116.
        Assert.Equal(1, ScripCap.DeliveriesThatFit([Orange(3881, 116), Purple(1139, 140)], 3));
        Assert.Equal(0, ScripCap.DeliveriesThatFit([Orange(3900, 116)], 3));
    }

    [Fact]
    public void The_tightest_currency_decides()
    {
        var rewards = new[] { Orange(3500, 116), Purple(3900, 140) };
        Assert.Equal(0, ScripCap.DeliveriesThatFit(rewards, 3));
        Assert.Equal("Purple Gatherers' Scrip", ScripCap.Limiting(rewards, 3)?.Currency);
    }

    [Fact]
    public void Nothing_limits_when_everything_fits()
    {
        Assert.Null(ScripCap.Limiting([Orange(0, 116)], 6));
    }

    [Fact]
    public void Rewards_without_a_cap_never_limit()
    {
        Assert.Equal(6, ScripCap.DeliveriesThatFit([new CappedReward("Gil", 999_999_999, 0, 1000)], 6));
    }

    [Fact]
    public void A_currency_already_over_its_cap_allows_nothing()
    {
        Assert.Equal(0, ScripCap.DeliveriesThatFit([Orange(4000, 116)], 2));
    }
}
