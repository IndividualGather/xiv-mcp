using XivMcp.Api;
using XivMcp.Mcp;

namespace XivMcp.Tests;

public class PluginOwnerTests
{
    private static readonly string[] Installed = ["HelloMcp", "AutoDuty"];

    [Fact]
    public void An_installed_plugin_is_a_valid_owner_even_while_it_is_still_loading() =>
        Assert.Equal("HelloMcp", PluginOwners.Validate("HelloMcp", Installed));

    [Fact]
    public void The_installed_spelling_is_returned() => Assert.Equal("HelloMcp", PluginOwners.Validate("hellomcp", Installed));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_owner_is_rejected(string owner) =>
        Assert.Contains("internal name", Assert.Throws<ToolException>(() => PluginOwners.Validate(owner, Installed)).Message);

    [Fact]
    public void XivMcp_itself_is_rejected() => Assert.Throws<ToolException>(() => PluginOwners.Validate("XivMcp", [.. Installed, "XivMcp"]));

    [Fact]
    public void Unknown_plugins_are_rejected() =>
        Assert.Contains("NoSuchPlugin", Assert.Throws<ToolException>(() => PluginOwners.Validate("NoSuchPlugin", Installed)).Message);
}

public class PluginIdentityTests
{
    private static readonly string[] Installed = ["HelloMcp", "OtherPlugin"];

    [Fact]
    public void A_call_from_the_named_plugin_is_accepted()
    {
        Assert.Equal("HelloMcp", PluginOwners.Validate("hellomcp", Installed, actualCaller: "HelloMcp"));
    }

    [Fact]
    public void A_plugin_naming_another_plugin_as_owner_is_refused()
    {
        var ex = Assert.Throws<ToolException>(() => PluginOwners.Validate("HelloMcp", Installed, actualCaller: "OtherPlugin"));
        Assert.Contains("OtherPlugin", ex.Message);
    }

    [Fact]
    public void A_call_whose_plugin_cannot_be_told_is_refused()
    {
        Assert.Throws<ToolException>(() => PluginOwners.Validate("HelloMcp", Installed, actualCaller: null, requireCaller: true));
    }
}
