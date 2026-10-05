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
    public void Crafting_comes_first_unless_another_request_has_the_bonus()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe"), Npc("Adkiragh", bonus: DeliveryKind.Fish)], All, allowances: 12);
        Assert.Equal([DeliveryKind.Craft, DeliveryKind.Fish], plan.Select(p => p.Kind));
    }

    [Fact]
    public void Only_kinds_that_can_be_done_are_chosen()
    {
        var plan = DeliveryPlan.Choose([Npc("Zhloe", bonus: DeliveryKind.Craft)], new HashSet<DeliveryKind> { DeliveryKind.Gather }, allowances: 12);
        Assert.Equal(DeliveryKind.Gather, plan.Single().Kind);
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
            [Npc("Locked", unlocked: false), Npc("Done", craft: 0, gather: 0, fish: 0), Npc("Zhloe", craft: 3), Npc("Kurenai")], All, allowances: 5);
        Assert.Equal([("Zhloe", 3), ("Kurenai", 2)], plan.Select(p => (p.Npc.Name, p.Count)));
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
