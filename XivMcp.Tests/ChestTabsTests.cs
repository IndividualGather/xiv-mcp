using XivMcp.Transfers;

namespace XivMcp.Tests;

public class ChestTabsTests
{
    [Fact]
    public void Item_pages_are_numbered_from_their_container_name()
    {
        Assert.Equal(1, ChestTabs.Page("FreeCompanyPage1"));
        Assert.Equal(5, ChestTabs.Page("FreeCompanyPage5"));
        Assert.Null(ChestTabs.Page("FreeCompanyCrystals"));
        Assert.Null(ChestTabs.Page("Inventory1"));
        Assert.Null(ChestTabs.Page(null));
    }

    [Fact]
    public void Each_page_has_its_tab_in_the_chest_window()
    {
        // FreeCompanyChest: the item tabs are nodes 10-14, then Crystals (15) and Gil (16).
        Assert.Equal(10u, ChestTabs.TabNode(1));
        Assert.Equal(14u, ChestTabs.TabNode(5));
    }

    [Fact]
    public void A_move_needs_the_pages_it_takes_from_and_puts_into()
    {
        Assert.Equal([2], ChestTabs.PagesFor("FreeCompanyPage2", "Inventory1", searchesAll: false));
        Assert.Equal([4], ChestTabs.PagesFor("Inventory1", "FreeCompanyPage4", searchesAll: false));
        Assert.Empty(ChestTabs.PagesFor("Inventory1", "Inventory2", searchesAll: false));
    }

    [Fact]
    public void Finding_an_item_anywhere_needs_every_page()
    {
        Assert.Equal([1, 2, 3, 4, 5], ChestTabs.PagesFor(null, "Inventory1", searchesAll: true));
        Assert.Equal([1, 2, 3, 4, 5], ChestTabs.PagesFor(null, "FreeCompanyPage3", searchesAll: true));
    }
}
