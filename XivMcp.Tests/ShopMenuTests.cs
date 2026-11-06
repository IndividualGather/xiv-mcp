using System.Collections.Generic;
using XivMcp.Shops;

namespace XivMcp.Tests;

public class ShopMenuTests
{
    // Syneyhil (Limsa Lominsa Lower Decks): a topic menu first, then the shops behind the chosen topic.
    private static readonly List<string> TopLevel =
        ["Purchase Disciple of the Hand Tools", "Purchase Disciple of the Land Tools", "Purchase Items", "Nothing"];

    private static readonly List<string> HandTools =
        ["Purchase Tools (Lv. 1-9)", "Purchase Tools (Lv. 10-19)", "Purchase Tools (Lv. 20-29)", "Cancel"];

    private static readonly List<string> ShopNames = ["Purchase Disciple of the Hand Tools", "Purchase Tools (Lv. 1-9)"];

    [Fact]
    public void APathIsSplitAtTheArrows()
    {
        Assert.Equal(["Purchase Disciple of the Hand Tools", "Purchase Tools (Lv. 1-9)"],
                     ShopMenu.ParsePath(" Purchase Disciple of the Hand Tools > Purchase Tools (Lv. 1-9) "));
        Assert.Equal(["Purchase Items"], ShopMenu.ParsePath("Purchase Items"));
        Assert.Empty(ShopMenu.ParsePath(null));
        Assert.Empty(ShopMenu.ParsePath("  >  "));
    }

    [Fact]
    public void TheShopNamesLeadThroughATopicMenu()
    {
        var used = new List<string>();
        var first = ShopMenu.Pick(TopLevel, [], ShopNames, used);
        Assert.Equal(0, first?.Index);
        used.Add(first!.Matched!);
        var second = ShopMenu.Pick(HandTools, [], ShopNames, used);
        Assert.Equal(0, second?.Index);
    }

    [Fact]
    public void AMenuOptionForTheFirstLevelFallsBackToTheShopNamesBelowIt()
    {
        // menu_option "Purchase Disciple of the Hand Tools" used to be tried again on the shop list, which doesn't have it.
        List<string> path = ["Purchase Disciple of the Hand Tools"];
        var used = new List<string>();
        var first = ShopMenu.Pick(TopLevel, path, ShopNames, used);
        Assert.Equal(0, first?.Index);
        used.Add(first!.Matched!);
        Assert.Equal(0, ShopMenu.Pick(HandTools, path, ShopNames, used)?.Index);
    }

    [Fact]
    public void APathPicksAnEntryAtEachLevel()
    {
        var path = ShopMenu.ParsePath("Purchase Disciple of the Hand Tools > Purchase Tools (Lv. 10-19)");
        var used = new List<string>();
        var first = ShopMenu.Pick(TopLevel, path, [], used);
        Assert.Equal(0, first?.Index);
        used.Add(first!.Matched!);
        Assert.Equal(1, ShopMenu.Pick(HandTools, path, [], used)?.Index);
    }

    [Fact]
    public void ThePathComesBeforeTheShopNames()
    {
        var path = ShopMenu.ParsePath("Purchase Tools (Lv. 20-29)");
        Assert.Equal(2, ShopMenu.Pick(HandTools, path, ShopNames, [])?.Index);
    }

    [Fact]
    public void ASecondLevelNameAloneDoesNotGuessTheFirstLevel()
    {
        // Three entries look like buying: without a matching name, the caller has to say which.
        Assert.Null(ShopMenu.Pick(TopLevel, ShopMenu.ParsePath("Purchase Tools (Lv. 1-9)"), [], []));
    }

    [Fact]
    public void TheOnlyBuyingEntryIsPickedWithoutNames()
    {
        Assert.Equal(1, ShopMenu.Pick(["Ask about the town", "Purchase Items", "Nothing"], [], [], [])?.Index);
    }

    [Fact]
    public void AnEntryNameIsMatchedByPrefixAndIgnoringCase()
    {
        Assert.Equal(0, ShopMenu.Pick(["Purchase Items (Lv. 1-50)", "Nothing"], ["purchase items"], [], [])?.Index);
    }

    [Fact]
    public void AUsedNameIsNotMatchedAgain()
    {
        // The same text on the next level would loop back into the topic.
        List<string> menu = ["Purchase Disciple of the Hand Tools", "Purchase Tools (Lv. 1-9)"];
        Assert.Equal(1, ShopMenu.Pick(menu, [], ShopNames, ["Purchase Disciple of the Hand Tools"])?.Index);
    }

    [Theory]
    [InlineData(3, 1, 1)]     // gear doesn't stack: one at a time
    [InlineData(150, 99, 99)] // stackable: up to 99 per batch
    [InlineData(20, 999, 20)]
    [InlineData(0, 99, 0)]
    public void BatchSizeDependsOnStacking(int remaining, int stackSize, int expected) =>
        Assert.Equal(expected, ShopMenu.BatchSize(remaining, stackSize, 99));
}
