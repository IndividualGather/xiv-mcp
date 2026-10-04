using XivMcp.Permissions;

namespace XivMcp.Tests;

public class PluginStatusTests
{
    [Fact]
    public void Undecided() => Assert.Equal(PluginState.Undecided, PluginStatus.Of(new PluginPolicy()).State);

    [Fact]
    public void Kept_disabled()
    {
        var p = new PluginPolicy();
        RegistrationReview.KeepDisabled(p, ["a_tool"], [Capabilities.ReadGame]);
        Assert.Equal(PluginState.KeptDisabled, PluginStatus.Of(p).State);
    }

    [Fact]
    public void Enabled_awaiting_consent_and_suspended()
    {
        Assert.Equal(PluginState.Enabled, PluginStatus.Of(new PluginPolicy { Enabled = true }).State);
        Assert.Equal(PluginState.AwaitingConsent, PluginStatus.Of(new PluginPolicy { Enabled = true, AwaitingConsent = true }).State);
        var s = PluginStatus.Of(new PluginPolicy { Enabled = true, Suspended = true, SuspendReason = "spent gil" });
        Assert.Equal(PluginState.Suspended, s.State);
        Assert.Equal("spent gil", s.SuspendReason);
    }

    [Fact]
    public void Only_the_enabled_state_can_run_tools()
    {
        Assert.True(PluginStatus.Of(new PluginPolicy { Enabled = true }).CanRun);
        Assert.False(PluginStatus.Of(new PluginPolicy { Enabled = true, AwaitingConsent = true }).CanRun);
        Assert.False(PluginStatus.Of(new PluginPolicy { Enabled = true, Suspended = true }).CanRun);
        Assert.False(PluginStatus.Of(new PluginPolicy()).CanRun);
    }

    [Theory]
    [InlineData(PluginState.Undecided, "undecided")]
    [InlineData(PluginState.KeptDisabled, "kept_disabled")]
    [InlineData(PluginState.AwaitingConsent, "awaiting_consent")]
    public void States_have_stable_ids(PluginState state, string id) => Assert.Equal(id, PluginStatus.Id(state));
}
