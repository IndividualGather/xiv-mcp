using XivMcp.Permissions;

namespace XivMcp.Tests;

public class SideEffectAnalyzerTests
{
    private static GameSnapshot Snap(long gil = 1000, uint territory = 129, uint world = 66, long items = 50, int chat = 0, ulong contentId = 1,
                                     bool loggedIn = true, Dictionary<uint, long>? currencies = null) =>
        new(loggedIn, contentId, territory, world, gil, currencies ?? new Dictionary<uint, long> { [28] = 500 }, items, chat);

    [Fact]
    public void Nothing_changed_nothing_reported() => Assert.Empty(SideEffectAnalyzer.Analyze(Snap(), Snap(), [Capabilities.ReadGame]));

    [Fact]
    public void Gains_are_not_side_effects() =>
        Assert.Empty(SideEffectAnalyzer.Analyze(Snap(), Snap(gil: 5000, items: 60, currencies: new() { [28] = 900 }), [Capabilities.ReadGame]));

    [Fact]
    public void Spending_gil_is_covered_by_spend_gil()
    {
        var e = Assert.Single(SideEffectAnalyzer.Analyze(Snap(gil: 1000), Snap(gil: 400), [Capabilities.SpendGil]));
        Assert.Equal("spent_gil", e.Effect);
        Assert.Contains("600", e.Detail);
        Assert.False(e.Undeclared);
    }

    [Fact]
    public void Spending_gil_undeclared_is_flagged() =>
        Assert.True(Assert.Single(SideEffectAnalyzer.Analyze(Snap(gil: 1000), Snap(gil: 400), [Capabilities.ReadGame])).Undeclared);

    [Fact]
    public void Spending_a_currency_needs_spend_currency()
    {
        var e = Assert.Single(SideEffectAnalyzer.Analyze(Snap(), Snap(currencies: new() { [28] = 100 }), [Capabilities.SpendGil]));
        Assert.Equal("spent_currency", e.Effect);
        Assert.True(e.Undeclared);
        Assert.Contains(Capabilities.SpendCurrency, e.CoveredBy);
    }

    [Theory]
    [InlineData(Capabilities.MoveItems)]
    [InlineData(Capabilities.TradeItems)]
    [InlineData(Capabilities.DiscardItems)]
    public void Items_leaving_the_inventory_are_covered_by_any_item_capability(string declared) =>
        Assert.False(Assert.Single(SideEffectAnalyzer.Analyze(Snap(items: 50), Snap(items: 40), [declared])).Undeclared);

    [Fact]
    public void Zone_change_needs_move_character_or_combat()
    {
        Assert.True(Assert.Single(SideEffectAnalyzer.Analyze(Snap(territory: 129), Snap(territory: 130), [Capabilities.GameUi])).Undeclared);
        Assert.False(Assert.Single(SideEffectAnalyzer.Analyze(Snap(territory: 129), Snap(territory: 1036), [Capabilities.Combat])).Undeclared);
        Assert.False(Assert.Single(SideEffectAnalyzer.Analyze(Snap(world: 66), Snap(world: 67), [Capabilities.MoveCharacter])).Undeclared);
    }

    [Fact]
    public void Sending_chat_needs_chat_send()
    {
        var e = Assert.Single(SideEffectAnalyzer.Analyze(Snap(chat: 3), Snap(chat: 5), [Capabilities.GameUi]));
        Assert.Equal("chat_sent", e.Effect);
        Assert.True(e.Undeclared);
    }

    [Fact]
    public void Logging_out_or_switching_character_needs_login_and_skips_the_rest()
    {
        var logout = Assert.Single(SideEffectAnalyzer.Analyze(Snap(), Snap(loggedIn: false, gil: 0, items: 0, territory: 0), [Capabilities.ReadGame]));
        Assert.Equal("logged_out_or_switched", logout.Effect);
        Assert.True(logout.Undeclared);

        var other = Assert.Single(SideEffectAnalyzer.Analyze(Snap(contentId: 1), Snap(contentId: 2, gil: 5), [Capabilities.Login]));
        Assert.False(other.Undeclared);
    }

    [Fact]
    public void Not_logged_in_before_means_nothing_to_compare() =>
        Assert.Empty(SideEffectAnalyzer.Analyze(Snap(loggedIn: false), Snap(gil: 1), [Capabilities.ReadGame]));
}
