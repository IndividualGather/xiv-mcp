using XivMcp.Permissions;

namespace XivMcp.Tests;

public class CapabilityTests
{
    [Fact]
    public void Catalog_has_unique_ids_titles_and_descriptions()
    {
        Assert.NotEmpty(Capabilities.All);
        Assert.Equal(Capabilities.All.Count, Capabilities.All.Select(c => c.Id).Distinct().Count());
        Assert.All(Capabilities.All, c =>
        {
            Assert.Matches("^[a-z_]+$", c.Id);
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
        });
    }

    [Theory]
    [InlineData(Capabilities.ReadGame, RiskLevel.Low)]
    [InlineData(Capabilities.GameUi, RiskLevel.Medium)]
    [InlineData(Capabilities.MoveCharacter, RiskLevel.Medium)]
    [InlineData(Capabilities.SpendGil, RiskLevel.High)]
    [InlineData(Capabilities.ChatSend, RiskLevel.High)]
    [InlineData(Capabilities.DiscardItems, RiskLevel.Critical)]
    public void Risk_levels(string id, RiskLevel risk) => Assert.Equal(risk, Capabilities.Find(id)!.Risk);

    [Fact]
    public void Unknown_capability_is_not_found() => Assert.Null(Capabilities.Find("fly_to_the_moon"));

    [Theory]
    [InlineData(RiskLevel.Low, PolicyMode.Allow)]
    [InlineData(RiskLevel.Medium, PolicyMode.Ask)]
    [InlineData(RiskLevel.High, PolicyMode.Ask)]
    [InlineData(RiskLevel.Critical, PolicyMode.Ask)]
    public void Default_modes_ask_for_anything_above_low(RiskLevel risk, PolicyMode mode) => Assert.Equal(mode, Capabilities.DefaultMode(risk));

    [Fact]
    public void Critical_capabilities_can_never_be_always_allowed()
    {
        Assert.False(Capabilities.IsModeAllowed(RiskLevel.Critical, PolicyMode.Allow));
        Assert.True(Capabilities.IsModeAllowed(RiskLevel.Critical, PolicyMode.Ask));
        Assert.True(Capabilities.IsModeAllowed(RiskLevel.High, PolicyMode.Allow));
    }
}

public class PluginPolicyTests
{
    [Fact]
    public void New_policy_is_disabled_and_uses_defaults()
    {
        var p = new PluginPolicy();
        Assert.False(p.Enabled);
        Assert.False(p.Suspended);
        Assert.Equal(PolicyMode.Allow, p.ModeFor(Capabilities.ReadGame));
        Assert.Equal(PolicyMode.Ask, p.ModeFor(Capabilities.SpendGil));
    }

    [Fact]
    public void Explicit_mode_wins_over_default()
    {
        var p = new PluginPolicy { Modes = { [Capabilities.SpendGil] = PolicyMode.Deny, [Capabilities.MoveCharacter] = PolicyMode.Allow } };
        Assert.Equal(PolicyMode.Deny, p.ModeFor(Capabilities.SpendGil));
        Assert.Equal(PolicyMode.Allow, p.ModeFor(Capabilities.MoveCharacter));
    }

    [Fact]
    public void A_stored_allow_for_a_critical_capability_is_treated_as_ask()
    {
        var p = new PluginPolicy { Modes = { [Capabilities.DiscardItems] = PolicyMode.Allow } };
        Assert.Equal(PolicyMode.Ask, p.ModeFor(Capabilities.DiscardItems));
    }

    [Fact]
    public void Unknown_capabilities_are_denied() => Assert.Equal(PolicyMode.Deny, new PluginPolicy().ModeFor("fly_to_the_moon"));
}
