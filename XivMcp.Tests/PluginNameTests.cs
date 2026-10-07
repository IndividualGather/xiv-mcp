using XivMcp.Api;

namespace XivMcp.Tests;

public class PluginNameTests
{
    [Theory]
    [InlineData("XivMcp.", "XivMcp")]
    [InlineData(" XivMcp .. ", "XivMcp")]
    [InlineData("Hello.Mcp", "Hello.Mcp")]
    public void Names_lose_what_Windows_drops_from_folder_names(string name, string clean) => Assert.Equal(clean, PluginNames.Clean(name));

    [Fact]
    public void A_trailing_dot_does_not_make_another_plugin() => Assert.True(PluginNames.Same("XivMcp.", "xivmcp"));
}
