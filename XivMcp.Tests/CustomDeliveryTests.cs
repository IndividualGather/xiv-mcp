using XivMcp.CustomDeliveries;

namespace XivMcp.Tests;

public class DeliveryPlanTests
{
    private static readonly HashSet<DeliveryKind> All = [DeliveryKind.Craft, DeliveryKind.Gather, DeliveryKind.Fish];

    private static DeliveryNpc Npc(string name, int craft = 6, int gather = 6, int fish = 6, bool unlocked = true, params DeliveryKind[] bonus) =>
        new(name, unlocked, [craft, gather, fish], bonus.ToHashSet());

    [Fact]
    public void Each_npc_gets_its_remaining_deliveries_until_the_weekly_allowance_runs_out()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe"), Npc("M'naago"), Npc("Kurenai")], All, allowances: 12);
        Assert.Equal([("Zhloe", 6), ("M'naago", 6)], plan.Select(p => (p.Npc.Name, p.Count)));
    }

    [Fact]
    public void A_client_gets_no_more_than_it_takes_this_week()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe") with { Left = 2 }], All, allowances: 12);
        Assert.Equal([(DeliveryKind.Craft, 2)], plan.Select(p => (p.Kind, p.Count)));
    }

    [Fact]
    public void Crafting_comes_first_then_gathering()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe"), Npc("Adkiragh", craft: 0)], All, allowances: 12);
        Assert.Equal([("Zhloe", DeliveryKind.Craft), ("Adkiragh", DeliveryKind.Gather)], plan.Select(p => (p.Npc.Name, p.Kind)));
    }

    [Fact]
    public void Clients_with_a_bonus_come_next_with_the_bonus_request_first()
    {
        var plan = DeliveryPlan.Choose(
            [Npc("Zhloe"), Npc("Adkiragh", craft: 3, gather: 3, bonus: DeliveryKind.Gather) with { Left = 6 }, Npc("Ameliance", bonus: DeliveryKind.Craft)],
            All, allowances: 12);
        Assert.Equal([("Adkiragh", DeliveryKind.Gather), ("Adkiragh", DeliveryKind.Craft), ("Ameliance", DeliveryKind.Craft)],
                     plan.Select(p => (p.Npc.Name, p.Kind)));
    }

    [Fact]
    public void A_fishing_bonus_counts_only_when_fishing()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe"), Npc("Ehll Tou", bonus: DeliveryKind.Fish)], All, allowances: 6);
        Assert.Equal(["Zhloe"], plan.Select(p => p.Npc.Name));
        plan = DeliveryPlan.Choose([Npc("Zhloe"), Npc("Ehll Tou", bonus: DeliveryKind.Fish)], All, allowances: 6, kind: DeliveryKind.Fish);
        Assert.Equal(["Ehll Tou"], plan.Select(p => p.Npc.Name));
    }

    [Fact]
    public void Then_clients_that_need_more_deliveries_for_their_achievement()
    {
        var plan = DeliveryPlan.Choose(
            [Npc("Zhloe") with { AchievementLeft = 0 }, Npc("Kurenai") with { AchievementLeft = 40 }, Npc("Anden") with { AchievementLeft = 90 },
             Npc("Margrat", bonus: DeliveryKind.Craft) with { AchievementLeft = 10 }],
            All, allowances: 18);
        Assert.Equal(["Margrat", "Anden", "Kurenai"], plan.Select(p => p.Npc.Name));
    }

    [Fact]
    public void Rank_ups_come_before_a_bonus()
    {
        var plan = DeliveryPlan.Choose([Npc("Ameliance", bonus: DeliveryKind.Craft), Npc("Tiisol Ja") with { GainsRank = true }], All, allowances: 6);
        Assert.Equal(["Tiisol Ja"], plan.Select(p => p.Npc.Name));
    }

    [Fact]
    public void Fishing_only_when_asked_for()
    {
        var onlyFish = new HashSet<DeliveryKind> { DeliveryKind.Fish };
        Assert.Empty(DeliveryPlan.Choose([Npc("Zhloe", bonus: DeliveryKind.Fish)], onlyFish, allowances: 12));
        Assert.Equal(DeliveryKind.Fish, DeliveryPlan.Choose([Npc("Zhloe")], onlyFish, allowances: 12, kind: DeliveryKind.Fish).Single().Kind);
    }

    [Fact]
    public void Only_kinds_that_can_be_done_are_chosen()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe")], new HashSet<DeliveryKind> { DeliveryKind.Craft }, allowances: 12);
        Assert.Equal(DeliveryKind.Craft, plan.Single().Kind);
    }

    [Fact]
    public void A_chosen_kind_is_used_for_everyone()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe", bonus: DeliveryKind.Craft), Npc("Ehll Tou")], All, allowances: 12, kind: DeliveryKind.Fish);
        Assert.All(plan, p => Assert.Equal(DeliveryKind.Fish, p.Kind));
    }

    [Fact]
    public void Locked_and_finished_npcs_are_skipped_and_a_partial_last_one_is_capped()
    {
        var plan = DeliveryPlan.Choose(
            [Npc("Locked", unlocked: false), Npc("Done", craft: 0, gather: 0, fish: 0), Npc("Zhloe", craft: 0, gather: 3), Npc("Kurenai")], All, allowances: 5);
        Assert.Equal([("Zhloe", 3), ("Kurenai", 2)], plan.Select(p => (p.Npc.Name, p.Count)));
    }

    [Fact]
    public void A_request_that_takes_fewer_than_the_client_still_takes_is_topped_up_with_gathering()
    {
        // Tiisol Ja at rank 4: six deliveries left this week, but each request takes only three more.
        var tiisol = Npc("Tiisol Ja", craft: 3, gather: 3, fish: 3) with { Left = 6 };
        var plan = DeliveryPlan.Choose([tiisol], All, allowances: 12);
        Assert.Equal([(DeliveryKind.Craft, 3), (DeliveryKind.Gather, 3)], plan.Select(p => (p.Kind, p.Count)));
    }

    [Fact]
    public void Crafting_stops_at_the_scrip_cap_and_gathering_takes_the_rest()
    {
        // 3,399/4,000 crafters' scrips, 270 per bonus delivery: two fit.
        var crafters = new CappedReward("Purple Crafters' Scrip", 3399, 4000, 270);
        var nito = Npc("Nitowikwe") with { Rewards = [[crafters], [], []] };
        var plan = DeliveryPlan.Choose([nito], All, allowances: 12);
        Assert.Equal([(DeliveryKind.Craft, 2), (DeliveryKind.Gather, 4)], plan.Select(p => (p.Kind, p.Count)));
    }

    [Fact]
    public void The_scrip_cap_is_shared_by_every_client_in_the_plan()
    {
        var crafters = new CappedReward("Purple Crafters' Scrip", 3399, 4000, 180);
        CappedReward[][] rewards = [[crafters], [], []];
        var plan = DeliveryPlan.Choose([Npc("Zhloe") with { Rewards = rewards }, Npc("Kurenai") with { Rewards = rewards }], All, allowances: 12);
        // 601 scrips fit three deliveries of 180: all three go to the first client.
        Assert.Equal([("Zhloe", DeliveryKind.Craft, 3), ("Zhloe", DeliveryKind.Gather, 3), ("Kurenai", DeliveryKind.Gather, 6)],
                     plan.Select(p => (p.Npc.Name, p.Kind, p.Count)));
    }

    [Fact]
    public void Clients_who_still_rank_up_come_first()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe"), Npc("Nitowikwe"), Npc("Tiisol Ja") with { GainsRank = true }], All, allowances: 12);
        Assert.Equal(["Tiisol Ja", "Zhloe"], plan.Select(p => p.Npc.Name));
    }

    [Fact]
    public void A_chosen_kind_is_not_topped_up_with_another()
    {
        var tiisol = Npc("Tiisol Ja", craft: 3, gather: 3, fish: 3) with { Left = 6 };
        var plan = DeliveryPlan.Choose([tiisol], All, allowances: 12, kind: DeliveryKind.Gather);
        Assert.Equal([(DeliveryKind.Gather, 3)], plan.Select(p => (p.Kind, p.Count)));
    }

    [Fact]
    public void Only_the_named_npcs_when_given()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe"), Npc("Kurenai")], All, allowances: 12, only: ["kurenai"]);
        Assert.Equal(["Kurenai"], plan.Select(p => p.Npc.Name));
    }

    [Fact]
    public void Nothing_to_do_without_allowances()
    {
        Assert.Empty(DeliveryPlan.Choose([Npc("Zhloe")], All, allowances: 0));
    }
}

public class DeliveryAchievementTests
{
    private static readonly DeliveryAchievement[] Sheet =
    [
        new(1774, "Satisfaction Guaranteed: Zhloe Aliapoh I", 1),
        new(1779, "The Customer Is Always Right: Zhloe Aliapoh I", 36),
        new(1784, "The Customer Is Always Right: Zhloe Aliapoh VI", 150),
        new(1782, "The Customer Is Always Right: Zhloe Aliapoh IV", 100),
        new(1800, "The Customer Is Always Right: M'naago VI", 150),
    ];

    [Fact]
    public void A_clients_series_is_asked_about_its_last_tier()
    {
        Assert.Equal(1784u, DeliveryAchievement.LastTier("Zhloe Aliapoh", Sheet)?.Id);
        Assert.Equal(1800u, DeliveryAchievement.LastTier("m'naago", Sheet)?.Id);
        Assert.Null(DeliveryAchievement.LastTier("Kurenai", Sheet));
    }

    [Theory]
    [InlineData(40u, 150u, 110)]
    [InlineData(150u, 150u, 0)]
    [InlineData(170u, 150u, 0)]
    public void Deliveries_left_are_what_the_counter_still_lacks(uint current, uint max, int left)
    {
        Assert.Equal(left, DeliveryAchievement.Left(current, max));
    }
}

public class CustomDeliveryModuleTests
{
    [Theory]
    [InlineData("get_custom_deliveries")]
    [InlineData("deliver_custom_delivery")]
    [InlineData("do_custom_deliveries")]
    [InlineData("stop_custom_delivery")]
    public void The_tools_come_with_Satisfier(string tool)
    {
        Assert.False(XivMcp.Integrations.IntegrationCatalog.IsAvailable(tool, _ => false));
        Assert.True(XivMcp.Integrations.IntegrationCatalog.IsAvailable(tool, id => id == "vsatisfy"));
    }

    [Fact]
    public void Delivering_names_the_plugins_that_do_each_kind()
    {
        var needs = XivMcp.Integrations.ToolRequirements.For("do_custom_deliveries").Select(r => r.PluginId).ToList();
        Assert.Equal(["vsatisfy", "Artisan", "Questionable", "AutoHook", "vnavmesh"], needs);
    }
}
