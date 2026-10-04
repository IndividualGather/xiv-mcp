using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

public class XivMcpWindowToolTests
{
    [Fact]
    public void Screenshots_have_their_own_module_that_asks_before_reading()
    {
        var screen = PermissionCatalog.Find("screen")!;
        Assert.Equal(PolicyMode.Ask, screen.DefaultRead);
        Assert.False(screen.HasWrite);
        Assert.Equal("screen", PermissionCatalog.CoreTools["take_screenshot"]);
    }

    [Fact]
    public void Showing_the_window_is_a_game_and_navigation_change()
    {
        Assert.Equal("game_navigation", PermissionCatalog.CoreTools["show_xivmcp_window"]);
    }

    [Fact]
    public void Pressing_controls_is_a_dev_tool_with_a_module_like_any_other()
    {
        Assert.Contains("press_xivmcp_control", PermissionCatalog.DevTools);
        Assert.All(PermissionCatalog.DevTools, t => Assert.True(PermissionCatalog.CoreTools.ContainsKey(t), t));
    }

    [Fact]
    public void The_new_tools_have_player_summaries()
    {
        Assert.StartsWith("Looks at ", ToolSummaries.For("take_screenshot"));
        Assert.NotNull(ToolSummaries.For("show_xivmcp_window"));
        Assert.NotNull(ToolSummaries.For("press_xivmcp_control"));
    }
}
