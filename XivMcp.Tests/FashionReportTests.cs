using XivMcp.Fashion;
using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

public class GearSlotTests
{
    // Field order of the EquipSlotCategory sheet: main hand, off hand, head, body, gloves, waist, legs, feet, ears, neck, wrists,
    // left ring, right ring, soul crystal. 1 = goes there, -1 = blocks it.
    private static sbyte[] Category(params (int Field, sbyte Value)[] set)
    {
        var flags = new sbyte[14];
        foreach (var (field, value) in set) flags[field] = value;
        return flags;
    }

    [Fact]
    public void Simple_pieces_go_to_their_slot()
    {
        Assert.Equal(GearSlots.Feet, GearSlots.Target(Category((7, 1))));
        Assert.Equal(GearSlots.Head, GearSlots.Target(Category((2, 1))));
    }

    [Fact]
    public void Two_handed_weapons_go_to_the_main_hand()
    {
        Assert.Equal(GearSlots.MainHand, GearSlots.Target(Category((0, 1), (1, -1))));
    }

    [Fact]
    public void A_body_that_also_covers_the_legs_goes_to_the_body()
    {
        Assert.Equal(GearSlots.Body, GearSlots.Target(Category((3, 1), (6, -1))));
    }

    [Fact]
    public void Rings_take_the_free_ring_slot()
    {
        var ring = Category((11, 1), (12, 1));
        Assert.Equal(GearSlots.RingRight, GearSlots.Target(ring));
        Assert.Equal(GearSlots.RingLeft, GearSlots.Target(ring, occupied: s => s == GearSlots.RingRight));
    }

    [Fact]
    public void Items_that_are_not_gear_have_no_slot()
    {
        Assert.Null(GearSlots.Target(Category()));
    }

    [Theory]
    [InlineData("weapon", GearSlots.MainHand)]
    [InlineData("feet", GearSlots.Feet)]
    [InlineData("ear", GearSlots.Ears)]
    [InlineData("wrist", GearSlots.Wrists)]
    [InlineData("ring", GearSlots.RingRight)]
    public void Fashion_report_slot_names_map_to_gear_slots(string name, int slot)
    {
        Assert.Equal(slot, GearSlots.FromName(name));
    }
}

public class WeeklyReportTests
{
    private const string Json = """
        {"lastOptions":{"week":"453","reportTitle":"Overslept the Revolution","hints":[{"hint":"Robed in Resistance","slot":"body","ringNote":"none"},{"hint":"Dressed for Success","slot":"feet","ringNote":"none"}]},
         "dyeData":{"feet":{"plus1":"black","plus2":"Soot Black"}},
         "easy100":{"itemPairs":[{"slot":"body","name":"Filibuster's Heavy Gambison of Fending"},{"slot":"feet","name":"Cotton Dress Shoes"}],"dyes":{"head":"Snow White Dye","body":"","feet":"Soot Black"}},
         "easy80":{"itemPairs":[{"slot":"feet","name":"Cotton Dress Shoes"}],"dyes":{"weapon":"","feet":"Soot Black"}},
         "links":{"theorycraft":"https://example.com/theory"},"easy80Fresh":true,"easy100Fresh":false}
        """;

    [Fact]
    public void The_week_theme_and_hints_are_read()
    {
        var report = WeeklyReport.Parse(Json);
        Assert.Equal(453, report.Week);
        Assert.Equal("Overslept the Revolution", report.Theme);
        Assert.Equal(["Robed in Resistance", "Dressed for Success"], report.Hints.Select(h => h.Hint));
        Assert.Equal("feet", report.Hints[1].Slot);
    }

    [Fact]
    public void A_set_has_its_pieces_with_their_dyes()
    {
        var easy80 = WeeklyReport.Parse(Json).Easy80;
        var shoes = Assert.Single(easy80.Pieces);
        Assert.Equal(("feet", "Cotton Dress Shoes", "Soot Black"), (shoes.Slot, shoes.Item, shoes.Dye));
        Assert.True(easy80.Fresh);
    }

    [Fact]
    public void Dye_names_drop_the_word_dye_and_dyes_for_slots_without_a_piece_are_listed_apart()
    {
        var easy100 = WeeklyReport.Parse(Json).Easy100;
        Assert.Null(easy100.Pieces.Single(p => p.Slot == "body").Dye);
        Assert.Equal(("head", "Snow White"), Assert.Single(easy100.OtherDyes));
        Assert.False(easy100.Fresh);
    }
}

public class FashionSourceTests
{
    private static ItemWhereabouts Where(bool bags = false, string? retainer = null, bool dresser = false, bool armoire = false,
                                         bool vendor = false, bool duty = false, bool market = false) =>
        new(bags, retainer, dresser, armoire, vendor, duty, market);

    [Fact]
    public void An_item_you_carry_needs_nothing()
    {
        Assert.Equal(ItemSource.Have, FashionPlan.Choose(Where(bags: true, retainer: "Retainer A", vendor: true)));
    }

    [Fact]
    public void Stored_items_come_before_buying()
    {
        Assert.Equal(ItemSource.Retainer, FashionPlan.Choose(Where(retainer: "Retainer A", dresser: true, vendor: true)));
        Assert.Equal(ItemSource.GlamourDresser, FashionPlan.Choose(Where(dresser: true, armoire: true, vendor: true)));
        Assert.Equal(ItemSource.Armoire, FashionPlan.Choose(Where(armoire: true, vendor: true)));
    }

    [Fact]
    public void The_market_board_comes_after_vendors_and_before_dungeons()
    {
        Assert.Equal(ItemSource.Vendor, FashionPlan.Choose(Where(vendor: true, market: true)));
        Assert.Equal(ItemSource.Market, FashionPlan.Choose(Where(market: true, duty: true)));
    }

    [Fact]
    public void Buying_comes_before_running_a_dungeon_and_nothing_means_missing()
    {
        Assert.Equal(ItemSource.Vendor, FashionPlan.Choose(Where(vendor: true, duty: true)));
        Assert.Equal(ItemSource.Duty, FashionPlan.Choose(Where(duty: true)));
        Assert.Equal(ItemSource.None, FashionPlan.Choose(Where()));
    }
}

public class GoldSaucerStandaloneTests
{
    [Fact]
    public void Fashion_report_tools_need_no_plugin()
    {
        foreach (var tool in new[] { "present_fashion_report", "complete_fashion_report" })
        {
            Assert.True(IntegrationCatalog.IsAvailable(tool, _ => false), tool);
            Assert.Empty(ToolRequirements.For(tool).Where(r => r.Need == Need.Needed));
            Assert.Equal("saucy", PermissionCatalog.GroupOf(new McpTool { Name = tool, Description = "", Handler = (_, _) => Task.FromResult<object?>(null) }
                .WithProvider(IntegrationCatalog.For(tool)!.Provider, []))!.Id);
        }
    }

    [Fact]
    public void Saucy_tools_still_need_saucy()
    {
        Assert.False(IntegrationCatalog.IsAvailable("play_triple_triad", _ => false));
    }

    [Fact]
    public void The_gold_saucer_card_shows_without_any_plugin()
    {
        Assert.True(PermissionCatalog.Find("saucy")!.Standalone);
        Assert.False(PermissionCatalog.Find("autoduty")!.Standalone);
    }

    [Fact]
    public void The_new_core_tools_have_their_modules_and_summaries()
    {
        Assert.Equal("items_retainers", PermissionCatalog.CoreTools["equip_items"]);
        Assert.Equal("items_retainers", PermissionCatalog.CoreTools["retrieve_glamour_item"]);
        Assert.Equal("online", PermissionCatalog.CoreTools["get_fashion_report"]);
        foreach (var t in new[] { "equip_items", "retrieve_glamour_item", "get_fashion_report", "present_fashion_report", "complete_fashion_report" })
            Assert.NotNull(ToolSummaries.For(t));
    }
}

public class FashionScheduleTests
{
    [Theory]
    [InlineData("2026-10-02T08:00:00Z", true)]   // Friday 08:00: judging opens
    [InlineData("2026-10-04T16:00:00Z", true)]   // Sunday
    [InlineData("2026-10-06T07:59:00Z", true)]   // Tuesday just before the reset
    [InlineData("2026-10-06T08:00:00Z", false)]  // the weekly reset
    [InlineData("2026-10-08T12:00:00Z", false)]  // Thursday
    [InlineData("2026-10-02T07:59:00Z", false)]  // Friday just before
    public void Judging_runs_from_friday_until_the_tuesday_reset(string utc, bool open)
    {
        Assert.Equal(open, FashionSchedule.IsJudgingOpen(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal)));
    }
}

public class FashionCleanupTests
{
    [Theory]
    [InlineData(ItemSource.Vendor, 783, null, true)]
    [InlineData(ItemSource.Vendor, 5000, null, true)]
    [InlineData(ItemSource.Vendor, 5001, null, false)]
    [InlineData(ItemSource.Market, null, 3200, true)]
    [InlineData(ItemSource.Market, null, 48000, false)]
    public void Cheap_bought_pieces_are_discarded(ItemSource source, int? vendor, int? market, bool discard)
    {
        Assert.Equal(discard, FashionCleanup.ShouldDiscard(source, vendor, market, 5000));
    }

    [Theory]
    [InlineData(ItemSource.Have)]
    [InlineData(ItemSource.Retainer)]
    [InlineData(ItemSource.GlamourDresser)]
    [InlineData(ItemSource.Armoire)]
    [InlineData(ItemSource.Duty)]
    public void Pieces_that_were_not_bought_are_kept(ItemSource source)
    {
        Assert.False(FashionCleanup.ShouldDiscard(source, 100, 100, 5000));
    }

    [Fact]
    public void A_piece_of_unknown_value_is_kept()
    {
        Assert.False(FashionCleanup.ShouldDiscard(ItemSource.Market, null, null, 5000));
    }

    [Fact]
    public void The_vendor_price_counts_before_the_market_price()
    {
        Assert.True(FashionCleanup.ShouldDiscard(ItemSource.Vendor, 900, 20000, 5000));
    }
}
