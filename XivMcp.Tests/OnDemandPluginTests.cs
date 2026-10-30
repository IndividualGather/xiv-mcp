using System;
using System.Collections.Generic;
using XivMcp.Integrations;

namespace XivMcp.Tests;

public class OnDemandPluginTests
{
    private static Func<string, PluginInstall> Presence(Dictionary<string, PluginInstall> plugins) =>
        id => plugins.TryGetValue(id, out var p) ? p : PluginInstall.Missing;

    [Fact]
    public void A_needed_plugin_that_is_off_is_turned_on()
    {
        var off = Presence(new() { ["vsatisfy"] = PluginInstall.Off });
        Assert.Contains("vsatisfy", OnDemandPlugins.ToTurnOn("do_custom_deliveries", off));
    }

    [Fact]
    public void Loaded_and_missing_plugins_are_left_alone()
    {
        var plugins = Presence(new() { ["vsatisfy"] = PluginInstall.Loaded });
        Assert.DoesNotContain("vsatisfy", OnDemandPlugins.ToTurnOn("do_custom_deliveries", plugins));
        Assert.Empty(OnDemandPlugins.ToTurnOn("do_custom_deliveries", Presence([])));
    }

    [Fact]
    public void Helpers_are_turned_on_too()
    {
        // A delivery crafts with Artisan and walks with vnavmesh: the task runs by itself only with them.
        var plugins = Presence(new() { ["vsatisfy"] = PluginInstall.Loaded, ["Artisan"] = PluginInstall.Off, ["vnavmesh"] = PluginInstall.Off });
        Assert.Equal(["Artisan", "vnavmesh"], OnDemandPlugins.ToTurnOn("do_custom_deliveries", plugins));
    }

    [Fact]
    public void One_of_several_alternatives_is_enough()
    {
        // Reading Triple Triad cards works with Saucy or TriadBuddy.
        var oneLoaded = Presence(new() { ["Saucy"] = PluginInstall.Off, ["TriadBuddy"] = PluginInstall.Loaded });
        Assert.Empty(OnDemandPlugins.ToTurnOn("list_triad_cards", oneLoaded));
        var bothOff = Presence(new() { ["Saucy"] = PluginInstall.Off, ["TriadBuddy"] = PluginInstall.Off });
        Assert.Equal(["Saucy"], OnDemandPlugins.ToTurnOn("list_triad_cards", bothOff));
    }

    [Fact]
    public void Core_tools_that_drive_another_plugins_automation_turn_it_on()
    {
        Assert.Equal(["AutoRetainer"], OnDemandPlugins.ToTurnOn("run_autoretainer", Presence(new() { ["AutoRetainer"] = PluginInstall.Off })));
    }

    [Fact]
    public void Tools_without_plugins_turn_nothing_on()
    {
        Assert.Empty(OnDemandPlugins.ToTurnOn("get_inventory", Presence(new() { ["vnavmesh"] = PluginInstall.Off })));
    }
}

public class PluginLeaseTests
{
    private static readonly DateTime T0 = new(2026, 10, 30, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Plugins_go_off_once_nothing_has_run_for_a_while()
    {
        var leases = new PluginLeases();
        leases.Add("vsatisfy", T0);
        Assert.Empty(leases.Due(busy: false, T0 + PluginLeases.IdleBefore - TimeSpan.FromSeconds(1)));
        Assert.Equal(["vsatisfy"], leases.Due(busy: false, T0 + PluginLeases.IdleBefore));
        Assert.Empty(leases.Due(busy: false, T0 + PluginLeases.IdleBefore * 2));
    }

    [Fact]
    public void A_running_job_keeps_them_on()
    {
        var leases = new PluginLeases();
        leases.Add("Questionable", T0);
        Assert.Empty(leases.Due(busy: true, T0.AddHours(1)));
        // The wait starts over when the job ends.
        Assert.Empty(leases.Due(busy: false, T0.AddHours(1).AddSeconds(30)));
        Assert.Equal(["Questionable"], leases.Due(busy: false, T0.AddHours(1) + PluginLeases.IdleBefore));
    }

    [Fact]
    public void Only_what_XIV_MCP_turned_on_is_turned_off()
    {
        var leases = new PluginLeases();
        Assert.Empty(leases.Due(busy: false, T0.AddHours(1)));
        leases.Add("Artisan", T0);
        leases.Add("artisan", T0);
        Assert.Equal(["Artisan"], leases.Due(busy: false, T0.AddHours(1)));
    }
}

public class PluginCollectionTests
{
    private static readonly PluginCollection Default = new("Default", Enabled: true, IsDefault: true, ["XivMcp", "SimpleTweaks"]);
    private static readonly PluginCollection Automate = new("Automate", Enabled: false, IsDefault: false, ["AutoRetainer", "Questionable", "vnavmesh"]);

    [Fact]
    public void A_collection_is_found_by_name()
    {
        Assert.Same(Automate, PluginCollection.Find([Default, Automate], "automate"));
        Assert.Throws<ArgumentException>(() => PluginCollection.Find([Default, Automate], "Raids"));
    }

    [Fact]
    public void Turning_a_collection_on_or_off_is_fine()
    {
        Assert.Null(PluginCollection.Refusal(Automate, enable: true, [Default, Automate], self: "XivMcp"));
        Assert.Null(PluginCollection.Refusal(Automate with { Enabled = true }, enable: false, [Default, Automate with { Enabled = true }], self: "XivMcp"));
    }

    [Fact]
    public void The_default_collection_is_always_on()
    {
        Assert.NotNull(PluginCollection.Refusal(Default, enable: false, [Default, Automate], self: "XivMcp"));
    }

    [Fact]
    public void A_collection_that_alone_keeps_XIV_MCP_loaded_stays_on()
    {
        var mine = new PluginCollection("Mine", Enabled: true, IsDefault: false, ["XivMcp"]);
        var others = new[] { Default with { Plugins = ["SimpleTweaks"] }, mine };
        Assert.NotNull(PluginCollection.Refusal(mine, enable: false, others, self: "XivMcp"));
        // Also in the default collection: switching this one off doesn't unload it.
        Assert.Null(PluginCollection.Refusal(mine, enable: false, [Default, mine], self: "XivMcp"));
    }
}
