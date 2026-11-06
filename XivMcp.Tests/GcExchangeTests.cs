using XivMcp.Shops;

namespace XivMcp.Tests;

public class GcExchangeTests
{
    /// <summary>A fake GrandCompanyExchange window: rows of (item id, seals, rank needed, stackable, opens the currency dialog).</summary>
    private static Func<int, uint> Window(params (uint Item, uint Seals, uint Rank, bool Stack, bool Dialog)[] rows)
    {
        var values = new Dictionary<int, uint> { [GcExchange.CountIndex] = (uint)rows.Length };
        for (var i = 0; i < rows.Length; i++)
        {
            var at = GcExchange.ItemsStart + i;
            values[at + 50] = rows[i].Seals;
            values[at + 250] = rows[i].Stack ? 1u : 0u;
            values[at + 300] = rows[i].Item;
            values[at + 400] = rows[i].Rank;
            values[at + 450] = rows[i].Dialog ? 1u : 0u;
        }
        return i => values.TryGetValue(i, out var v) ? v : 0;
    }

    [Fact]
    public void Finds_the_row_of_the_item()
    {
        var window = Window((5530, 100, 1, true, false), (5558, 200, 1, true, false), (21072, 200, 1, true, true));
        var row = GcExchange.Find(window, 5558);
        Assert.NotNull(row);
        Assert.Equal(1, row.Index);
        Assert.Equal(200u, row.Seals);
        Assert.Equal(1u, row.RankNeeded);
        Assert.True(row.Stackable);
        Assert.False(row.CurrencyDialog);
    }

    [Fact]
    public void An_item_not_on_the_page_is_null()
    {
        Assert.Null(GcExchange.Find(Window((5530, 100, 1, true, false)), 5558));
    }

    [Fact]
    public void An_empty_page_is_null()
    {
        Assert.Null(GcExchange.Find(Window(), 5558));
    }

    [Fact]
    public void Materials_are_tried_first_on_every_rank_tab()
    {
        var pages = GcExchange.Pages().ToList();
        Assert.Equal(15, pages.Count);
        Assert.Equal((0, GcExchange.MaterialsTab), pages[0]);
        Assert.Equal(5, pages.Count(p => p.RankTab == 0));
        Assert.Equal(pages.Count, pages.Distinct().Count());
        Assert.All(pages.Where(p => p.Category == GcExchange.MaterialsTab), p => Assert.True(pages.IndexOf(p) < 3));
    }

    [Theory]
    [InlineData(1002387u, 1)] // Storm Quartermaster: Maelstrom
    [InlineData(1002393u, 2)] // Serpent Quartermaster: Order of the Twin Adder
    [InlineData(1002390u, 3)] // Flame Quartermaster: Immortal Flames
    public void Quartermasters_belong_to_their_grand_company(uint npc, int gc)
    {
        Assert.Equal(gc, GcExchange.GrandCompanyOf(npc));
    }

    [Fact]
    public void Other_npcs_belong_to_no_grand_company()
    {
        Assert.Null(GcExchange.GrandCompanyOf(1005633));
    }
}
