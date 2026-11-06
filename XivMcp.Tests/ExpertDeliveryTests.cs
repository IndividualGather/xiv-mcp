using System.Collections.Generic;
using XivMcp.Integrations;

namespace XivMcp.Tests;

public class ExpertDeliveryTests
{
    private const string Config = """
        {
          "OfflineData": [
            { "CID": 111, "Name": "Example Bagsonly", "GCDeliveryType": 1 },
            { "CID": 222, "Name": "Example Noset", "GCDeliveryType": 2 },
            { "CID": 333, "Name": "Someone", "GCDeliveryType": 0 },
            { "CID": 444, "Name": "Everything", "GCDeliveryType": 3 }
          ],
          "DefaultGCExchangePlan": { "Items": [ { "ItemID": 21072, "Quantity": 65000 } ], "FinalizeByPurchasing": true },
          "AutoGCContinuation": true
        }
        """;

    [Fact]
    public void Only_the_bags_setting_is_allowed()
    {
        // Hide Armoury Chest Items: only what is in the bags, never the armoury chest or gear set items.
        Assert.Null(ExpertDelivery.Refusal(Config, 111));
    }

    [Theory]
    [InlineData(222, "gear set")]       // Hide Gear Set Items: still offers the armoury chest
    [InlineData(444, "armoury chest")]  // Show All Items
    [InlineData(333, "turned off")]     // Disabled
    [InlineData(999, "doesn't know")]   // a character AutoRetainer hasn't seen
    public void Anything_looser_or_unknown_is_refused(ulong cid, string reason)
    {
        Assert.Contains(reason, ExpertDelivery.Refusal(Config, cid));
    }

    [Fact]
    public void Venture_purchases_are_read_from_the_exchange_plan()
    {
        var plan = ExpertDelivery.Plan(Config);
        Assert.Equal([21072u], plan.Buys);
        Assert.True(plan.BuysAfterwards);
        Assert.True(plan.ContinuesWhenCapped);
    }

    [Fact]
    public void Handed_in_is_what_left_the_bags()
    {
        var before = new Dictionary<uint, int> { [3122] = 1, [6300] = 1, [4868] = 141, [21072] = 10 };
        var after = new Dictionary<uint, int> { [6300] = 1, [4868] = 141, [21072] = 90 };
        Assert.Equal(new Dictionary<uint, int> { [3122] = 1 }, ExpertDelivery.Gone(before, after));
    }
}
