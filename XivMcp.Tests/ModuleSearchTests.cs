using XivMcp.Permissions;

namespace XivMcp.Tests;

public class ModuleSearchTests
{
    private static readonly ModuleSearch.Tool[] Tools =
    [
        new("get_position", "Looks at where you are: zone, area, map coordinates and the flag on your map."),
        new("set_map_flag", "Places the flag on your map at a spot in any zone, and can open the map."),
        new("buy_item", "Buys from an NPC vendor."),
    ];

    [Fact]
    public void An_empty_query_shows_everything_unfiltered()
    {
        var r = ModuleSearch.Match("  ", "Game & navigation", "Windows, NPCs, walking and travel.", Tools);
        Assert.True(r.Visible);
        Assert.Null(r.Tools);
    }

    [Fact]
    public void A_section_matching_by_name_shows_all_its_tools()
    {
        var r = ModuleSearch.Match("navigation", "Game & navigation", "Windows, NPCs, walking and travel.", Tools);
        Assert.True(r.Visible);
        Assert.Null(r.Tools);
    }

    [Fact]
    public void Tools_match_on_their_name_with_spaces_or_underscores_and_on_their_summary()
    {
        Assert.Equal(["set_map_flag"], ModuleSearch.Match("map flag set", "Game", "", Tools).Tools!);
        Assert.Equal(["set_map_flag"], ModuleSearch.Match("set_map", "Game", "", Tools).Tools!);
        Assert.Equal(["buy_item"], ModuleSearch.Match("vendor", "Game", "", Tools).Tools!);
        Assert.Equal(["get_position", "set_map_flag"], ModuleSearch.Match("FLAG", "Game", "", Tools).Tools!.Order());
    }

    [Fact]
    public void Every_word_has_to_match()
    {
        Assert.Equal(["get_position"], ModuleSearch.Match("flag coordinates", "Game", "", Tools).Tools!);
        Assert.False(ModuleSearch.Match("flag retainer", "Game", "", Tools).Visible);
    }

    [Fact]
    public void A_section_with_no_match_is_hidden() =>
        Assert.False(ModuleSearch.Match("crafting", "Game & navigation", "Windows, NPCs, walking and travel.", Tools).Visible);

    [Fact]
    public void Words_can_span_the_section_and_its_tools()
    {
        // "market vendor": the section is Market & purchases, the tool mentions a vendor.
        var r = ModuleSearch.Match("market vendor", "Market & purchases", "Buying and selling.", Tools);
        Assert.True(r.Visible);
        Assert.Equal(["buy_item"], r.Tools!);
    }
}
