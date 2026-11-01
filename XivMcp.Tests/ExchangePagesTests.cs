using System.Collections.Generic;
using XivMcp.Shops;

namespace XivMcp.Tests;

public class ExchangePagesTests
{
    // The Unified Scrip Exchange in Ul'dah (2026-11-01): category "Crafters' Scrip (Gear)" selected, values as the game filled them.
    private static readonly Dictionary<int, string> Window = new()
    {
        [3] = "Crafters' Scrip (Gear)",
        [4] = "Crafters' Scrip (Master Recipes/Materials/Misc.)",
        [5] = "Crafters' Scrip (Materia)",
        [6] = "Gatherers' Scrip (Gear)",
        [7] = "Gatherers' Scrip (Regional Folklore/Materials/Misc.)",
        [8] = "Gatherers' Scrip (Materia)",
        [96] = "Unified Scrip Exchange",
        [101] = "–-Select a subcategory–-",
        [102] = "Purple Scrip Exchange (Lv. 58/IL 130)",
        [103] = "Purple Scrip Exchange (Lv. 60/IL 200)",
        [104] = "Orange Scrip Exchange (Lv. 100/IL 700)",
        [105] = "Orange Scrip Exchange (Lv. 100/IL 720)",
        [106] = "Orange Scrip Exchange (Lv. 100/IL 750)",
    };

    private static string? Text(int i) => Window.TryGetValue(i, out var s) ? s : null;

    [Fact]
    public void Categories_are_read_up_to_the_first_gap()
    {
        var categories = ExchangePages.Categories(Text);
        Assert.Equal(6, categories.Count);
        Assert.Equal("Crafters' Scrip (Gear)", categories[0]);
        Assert.Equal("Gatherers' Scrip (Materia)", categories[5]);
    }

    [Fact]
    public void Sub_pages_keep_the_prompt_at_index_zero()
    {
        var subPages = ExchangePages.SubPages(Text);
        Assert.Equal(6, subPages.Count);
        Assert.Equal("–-Select a subcategory–-", subPages[0]);
        Assert.Equal("Orange Scrip Exchange (Lv. 100/IL 750)", subPages[5]);
    }

    [Fact]
    public void The_sub_page_is_found_by_the_shop_name_the_window_shows()
    {
        // Star Tech crafter gear is sold by the SpecialShop "Orange Scrip Exchange (Lv. 100/IL 750)": sub-page 5, not its data row index.
        var subPages = ExchangePages.SubPages(Text);
        Assert.Equal(5, ExchangePages.SubPageOf(subPages, ["Orange Scrip Exchange (Lv. 100/IL 750)"]));
        Assert.Equal(4, ExchangePages.SubPageOf(subPages, ["orange scrip exchange (lv. 100/il 720) "]));
    }

    [Fact]
    public void The_prompt_and_unknown_shops_are_no_sub_page()
    {
        var subPages = ExchangePages.SubPages(Text);
        Assert.Null(ExchangePages.SubPageOf(subPages, ["–-Select a subcategory–-"]));
        Assert.Null(ExchangePages.SubPageOf(subPages, ["Allagan Tomestones of Mathematics (DoW, IL 750)"]));
        Assert.Null(ExchangePages.SubPageOf([], ["Orange Scrip Exchange (Lv. 100/IL 750)"]));
    }

    [Fact]
    public void A_category_without_sub_pages_lists_none()
    {
        Assert.Empty(ExchangePages.SubPages(i => i == 3 ? "Gear" : null));
    }
}
