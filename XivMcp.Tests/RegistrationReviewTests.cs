using XivMcp.Permissions;

namespace XivMcp.Tests;

public class RegistrationReviewTests
{
    private static readonly string[] Tools = ["hellomcp_greet", "hellomcp_print"];
    private static readonly string[] Declared = [Capabilities.ReadGame, Capabilities.GameUi, Capabilities.SpendGil];

    [Fact]
    public void A_plugin_the_player_never_decided_on_needs_a_decision()
    {
        var r = RegistrationReview.Check(new PluginPolicy(), Tools, Declared);
        Assert.Equal(RegistrationReview.Kind.NewPlugin, r.What);
        Assert.Equal(Capabilities.SpendGil, r.Riskiest!.Id);
        Assert.Equal(Tools, r.NewTools);
    }

    [Fact]
    public void Keeping_it_disabled_is_remembered_while_the_registration_is_unchanged()
    {
        var policy = new PluginPolicy();
        RegistrationReview.KeepDisabled(policy, Tools, Declared);
        Assert.False(policy.Enabled);
        Assert.True(policy.Reviewed);
        Assert.Equal(RegistrationReview.Kind.None, RegistrationReview.Check(policy, Tools, Declared).What);
    }

    [Fact]
    public void Enabling_records_the_registration_too()
    {
        var policy = new PluginPolicy();
        RegistrationReview.Enable(policy, Tools, Declared);
        Assert.True(policy.Enabled);
        Assert.Equal(RegistrationReview.Kind.None, RegistrationReview.Check(policy, Tools, Declared).What);
    }

    [Fact]
    public void A_new_tool_is_a_changed_registration()
    {
        var policy = new PluginPolicy();
        RegistrationReview.KeepDisabled(policy, Tools, Declared);
        var r = RegistrationReview.Check(policy, [.. Tools, "hellomcp_new"], Declared);
        Assert.Equal(RegistrationReview.Kind.Changed, r.What);
        Assert.Equal(["hellomcp_new"], r.NewTools);
        Assert.Empty(r.New);
    }

    [Fact]
    public void A_new_capability_is_a_changed_registration_and_only_the_new_ones_are_listed()
    {
        var policy = new PluginPolicy { Enabled = true };
        RegistrationReview.MarkReviewed(policy, Tools, [Capabilities.ReadGame, Capabilities.GameUi]);
        var r = RegistrationReview.Check(policy, Tools, Declared);
        Assert.Equal(RegistrationReview.Kind.Changed, r.What);
        Assert.Equal([Capabilities.SpendGil], r.New.Select(c => c.Id));
        Assert.Empty(r.NewTools);
    }

    [Fact]
    public void Removing_tools_or_capabilities_is_not_news()
    {
        var policy = new PluginPolicy();
        RegistrationReview.KeepDisabled(policy, Tools, Declared);
        Assert.Equal(RegistrationReview.Kind.None, RegistrationReview.Check(policy, ["hellomcp_greet"], [Capabilities.ReadGame]).What);
    }

    [Fact]
    public void An_enabled_plugin_only_reading_is_never_news() =>
        Assert.Equal(RegistrationReview.Kind.None, RegistrationReview.Check(new PluginPolicy { Enabled = true }, Tools, [Capabilities.ReadGame]).What);

    [Fact]
    public void Unknown_capability_ids_are_ignored()
    {
        var policy = new PluginPolicy();
        RegistrationReview.KeepDisabled(policy, Tools, [Capabilities.ReadGame]);
        Assert.Equal(RegistrationReview.Kind.None, RegistrationReview.Check(policy, Tools, ["fly_to_the_moon"]).What);
    }

    [Fact]
    public void A_changed_registration_of_an_enabled_plugin_waits_for_consent_again()
    {
        var policy = new PluginPolicy();
        RegistrationReview.Enable(policy, Tools, [Capabilities.ReadGame, Capabilities.GameUi]);
        var r = RegistrationReview.OnRegistered(policy, Tools, Declared);
        Assert.Equal(RegistrationReview.Kind.Changed, r.What);
        Assert.True(policy.AwaitingConsent);
        Assert.True(policy.Enabled); // the player's choice is kept; consent is what's missing

        RegistrationReview.Enable(policy, Tools, Declared);
        Assert.False(policy.AwaitingConsent);
    }

    [Fact]
    public void An_unchanged_re_registration_keeps_the_consent()
    {
        var policy = new PluginPolicy();
        RegistrationReview.Enable(policy, Tools, Declared);
        Assert.Equal(RegistrationReview.Kind.None, RegistrationReview.OnRegistered(policy, ["hellomcp_greet"], [Capabilities.ReadGame]).What);
        Assert.False(policy.AwaitingConsent);
    }

    [Fact]
    public void A_disabled_plugin_never_waits_for_consent_it_just_asks()
    {
        var policy = new PluginPolicy();
        RegistrationReview.KeepDisabled(policy, Tools, [Capabilities.ReadGame]);
        Assert.Equal(RegistrationReview.Kind.Changed, RegistrationReview.OnRegistered(policy, Tools, Declared).What);
        Assert.False(policy.AwaitingConsent);
    }

    [Fact]
    public void Keeping_it_disabled_clears_a_pending_consent()
    {
        var policy = new PluginPolicy { Enabled = true, AwaitingConsent = true };
        RegistrationReview.KeepDisabled(policy, Tools, Declared);
        Assert.False(policy.AwaitingConsent);
        Assert.False(policy.Enabled);
    }

    [Fact]
    public void An_enabled_plugin_from_before_reviews_existed_needs_consent_once()
    {
        var policy = new PluginPolicy { Enabled = true };
        Assert.Equal(RegistrationReview.Kind.NewPlugin, RegistrationReview.OnRegistered(policy, Tools, Declared).What);
        Assert.True(policy.AwaitingConsent);
    }

    [Fact]
    public void A_newer_decision_replaces_the_remembered_registration()
    {
        var policy = new PluginPolicy();
        RegistrationReview.KeepDisabled(policy, [.. Tools, "hellomcp_old"], Declared);
        RegistrationReview.KeepDisabled(policy, Tools, Declared);
        // hellomcp_old was dropped from the remembered registration, so it coming back is news again.
        Assert.Equal(RegistrationReview.Kind.Changed, RegistrationReview.Check(policy, [.. Tools, "hellomcp_old"], Declared).What);
    }
}
