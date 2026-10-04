using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

/// <summary>Third-party tools follow their capabilities (the strictest one), unless the player set the tool on its own.</summary>
public class ThirdPartyToolModeTests
{
    private static readonly ToolProvider Third = new("HelloMcp", "Hello MCP", ProviderTrust.ThirdParty);

    private static McpTool Tool(string name, params string[] caps) => new()
    {
        Name = name, Description = "Does it.", Provider = Third, ReadOnly = caps.Length == 0,
        Capabilities = [Capabilities.ReadGame, .. caps], Handler = (_, _) => Task.FromResult<object?>("done"),
    };

    [Fact]
    public void Tools_that_only_read_follow_the_reading_setting()
    {
        var p = new PluginPolicy();
        Assert.Equal(PolicyMode.Allow, p.ModeForTool(Tool("look")));
        p.Modes[Capabilities.ReadGame] = PolicyMode.Ask;
        Assert.Equal(PolicyMode.Ask, p.ModeForTool(Tool("look")));
    }

    [Fact]
    public void Tools_that_change_something_follow_their_strictest_capability_not_reading()
    {
        var p = new PluginPolicy();
        p.Modes[Capabilities.ReadGame] = PolicyMode.Deny;
        p.Modes[Capabilities.GameUi] = PolicyMode.Allow;
        Assert.Equal(PolicyMode.Allow, p.ModeForTool(Tool("click", Capabilities.GameUi)));
        p.Modes[Capabilities.SpendGil] = PolicyMode.Deny;
        Assert.Equal(PolicyMode.Deny, p.ModeForTool(Tool("buy", Capabilities.GameUi, Capabilities.SpendGil)));
    }

    [Fact]
    public void A_tool_set_on_its_own_overrides_its_capabilities()
    {
        var p = new PluginPolicy();
        p.Modes[Capabilities.SpendGil] = PolicyMode.Deny;
        p.SetTool("buy", PolicyMode.Allow);
        Assert.Equal(PolicyMode.Allow, p.ModeForTool(Tool("buy", Capabilities.SpendGil)));
        Assert.Equal(PolicyMode.Deny, p.SectionMode(Tool("buy", Capabilities.SpendGil))); // what it would follow
        Assert.Equal(PolicyMode.Deny, p.ModeForTool(Tool("other", Capabilities.SpendGil)));
        p.SetTool("buy", null);
        Assert.Null(p.ToolMode("buy"));
        Assert.Equal(PolicyMode.Deny, p.ModeForTool(Tool("buy", Capabilities.SpendGil)));
    }

    [Fact]
    public void A_tool_with_a_critical_capability_is_never_more_than_ask()
    {
        var p = new PluginPolicy();
        p.SetTool("discard", PolicyMode.Allow);
        Assert.Equal(PolicyMode.Ask, p.ModeForTool(Tool("discard", Capabilities.DiscardItems)));
        p.SetTool("discard", PolicyMode.Deny);
        Assert.Equal(PolicyMode.Deny, p.ModeForTool(Tool("discard", Capabilities.DiscardItems)));
    }

    [Fact]
    public void A_tool_declaring_an_unknown_capability_is_denied_even_when_set_to_allow()
    {
        var p = new PluginPolicy();
        p.SetTool("moon", PolicyMode.Allow);
        Assert.Equal(PolicyMode.Deny, p.ModeForTool(Tool("moon", "fly_to_the_moon")));
    }

    [Fact]
    public void Runtime_mode_uses_the_tool_setting_first_then_the_capability()
    {
        var p = new PluginPolicy();
        p.Modes[Capabilities.SpendGil] = PolicyMode.Deny;
        Assert.Equal(PolicyMode.Deny, p.RuntimeMode(Tool("buy", Capabilities.SpendGil), Capabilities.SpendGil));
        p.SetTool("buy", PolicyMode.Allow);
        Assert.Equal(PolicyMode.Allow, p.RuntimeMode(Tool("buy", Capabilities.SpendGil), Capabilities.SpendGil));
        p.SetTool("discard", PolicyMode.Allow);
        Assert.Equal(PolicyMode.Ask, p.RuntimeMode(Tool("discard", Capabilities.DiscardItems), Capabilities.DiscardItems));
    }

    [Fact]
    public void The_section_of_a_tool_is_its_capabilities_beyond_reading_or_reading_alone()
    {
        Assert.Equal([Capabilities.ReadGame], PluginPolicy.Sections(Tool("look")));
        Assert.Equal([Capabilities.GameUi, Capabilities.SpendGil], PluginPolicy.Sections(Tool("buy", Capabilities.GameUi, Capabilities.SpendGil)));
    }
}
