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
    public void A_second_copy_of_a_ring_takes_the_ring_slot_without_it()
    {
        // Both ring slots wear old rings; the first Star Tech ring went into the left one. The second replaces the right one.
        var ring = Category((11, 1), (12, 1));
        Assert.Equal(GearSlots.RingRight, GearSlots.Target(ring, occupied: _ => true, holds: s => s == GearSlots.RingLeft));
        Assert.Equal(GearSlots.RingLeft, GearSlots.Target(ring, occupied: _ => true, holds: s => s == GearSlots.RingRight));
        // Both slots wear it already: nowhere left to put a third.
        Assert.Null(GearSlots.Target(ring, occupied: _ => true, holds: _ => true));
    }

    [Fact]
    public void Only_rings_can_be_worn_twice()
    {
        Assert.Equal(2, GearSlots.CopiesWearable(Category((11, 1), (12, 1))));
        Assert.Equal(1, GearSlots.CopiesWearable(Category((3, 1))));
        Assert.Equal(1, GearSlots.CopiesWearable(Category((0, 1), (1, -1))));
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

    [Fact]
    public void Every_slot_has_a_name_in_container_order()
    {
        Assert.Equal(GearSlots.SoulCrystal + 1, GearSlots.Names.Length);
        Assert.Equal("MainHand", GearSlots.Name(GearSlots.MainHand));
        Assert.Equal("Waist", GearSlots.Name(GearSlots.Waist));
        Assert.Equal("RightRing", GearSlots.Name(GearSlots.RingRight));
        Assert.Equal("LeftRing", GearSlots.Name(GearSlots.RingLeft));
        Assert.Equal("SoulCrystal", GearSlots.Name(GearSlots.SoulCrystal));
    }

    [Fact]
    public void A_slot_beyond_the_named_ones_is_shown_as_its_number()
    {
        Assert.Equal("14", GearSlots.Name(14));
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

    [Fact]
    public void Storing_in_the_armoire_is_an_items_tool_with_a_summary()
    {
        Assert.Equal("items_retainers", PermissionCatalog.CoreTools["store_in_armoire"]);
        Assert.NotNull(ToolSummaries.For("store_in_armoire"));
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

public class FashionWeekTests
{
    // Week 460 as fashionreportxiv.com showed it on Wednesday 2026-11-25: posted on Friday 2026-11-20, marked old after the reset.
    private static string Json(bool fresh = false, long? updated = 1795167893814, int week = 460) => $$"""
        {"lastOptions":{"week":"{{week}}","reportTitle":"Dread Sky Pirate Mod-beard","hints":[{"hint":"Jewels of Yellow","slot":"neck","ringNote":"none"}]},
         "easy100":{"itemPairs":[{"slot":"neck","name":"Sphene Choker"}],"dyes":{"hands":"Charcoal Grey"}{{(updated is { } u ? $",\"_updatedAt\":{u - 1000}" : "")}}},
         "easy80":{"itemPairs":[{"slot":"neck","name":"Sphene Choker"}],"dyes":{"hands":"Charcoal Grey","legs":"Charcoal Grey"}{{(updated is { } v ? $",\"_updatedAt\":{v}" : "")}}},
         "easy80Fresh":{{(fresh ? "true" : "false")}},"easy100Fresh":{{(fresh ? "true" : "false")}}}
        """;

    private static DateTime Utc(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static readonly DateTime Posted = Utc("2026-11-20T09:44:53.814Z");

    private static ReportStatus Status(string utc, bool fresh = false, long? updated = 1795167893814, int week = 460)
    {
        var report = WeeklyReport.Parse(Json(fresh, updated, week));
        return ReportStatus.Of(report, report.Easy80, Utc(utc));
    }

    [Fact]
    public void A_set_has_the_time_the_site_updated_it()
    {
        var report = WeeklyReport.Parse(Json());
        Assert.Equal(Posted, report.Easy80.PostedUtc);
        Assert.Null(WeeklyReport.Parse(Json(updated: null)).Easy80.PostedUtc);
    }

    [Theory]
    [InlineData("2026-11-25T10:00:00Z", "2026-11-24T08:00:00Z")] // Wednesday
    [InlineData("2026-11-24T08:00:00Z", "2026-11-24T08:00:00Z")] // the reset itself
    [InlineData("2026-11-24T07:59:00Z", "2026-11-17T08:00:00Z")] // just before it
    [InlineData("2026-11-23T20:00:00Z", "2026-11-17T08:00:00Z")] // Monday
    public void The_last_reset_is_the_tuesday_at_08_utc_before(string utc, string reset)
    {
        Assert.Equal(Utc(reset), FashionSchedule.LastReset(Utc(utc)));
    }

    [Fact]
    public void Before_friday_judging_opens_and_closes_ahead()
    {
        Assert.Equal((Utc("2026-11-27T08:00:00Z"), Utc("2026-12-01T08:00:00Z")), FashionSchedule.JudgingPeriod(Utc("2026-11-25T10:00:00Z")));
    }

    [Fact]
    public void While_judging_runs_it_opened_on_friday_and_closes_at_the_reset()
    {
        Assert.Equal((Utc("2026-11-20T08:00:00Z"), Utc("2026-11-24T08:00:00Z")), FashionSchedule.JudgingPeriod(Utc("2026-11-22T16:00:00Z")));
    }

    [Theory]
    [InlineData("2026-05-01T12:00:00Z", 431)] // the guides' "week of 5/1/2026 (Week 431)"
    [InlineData("2026-10-09T12:00:00Z", 454)]
    [InlineData("2026-11-24T07:59:00Z", 460)]
    [InlineData("2026-11-24T08:00:00Z", 461)]
    [InlineData("2026-11-25T10:00:00Z", 461)]
    public void Weeks_are_numbered_as_the_guides_number_them(string utc, int week)
    {
        Assert.Equal(week, FashionSchedule.WeekAt(Utc(utc)));
    }

    [Fact]
    public void During_its_own_week_a_fresh_set_is_this_weeks()
    {
        var status = Status("2026-11-21T10:00:00Z", fresh: true);
        Assert.True(status.HintsAreThisWeeks);
        Assert.True(status.SetIsThisWeeks);
        Assert.Equal(460, status.CurrentWeek);
        Assert.True(status.JudgingOpen);
    }

    [Fact]
    public void After_the_reset_the_site_is_a_week_behind()
    {
        var status = Status("2026-11-25T10:00:00Z");
        Assert.False(status.HintsAreThisWeeks);
        Assert.False(status.SetIsThisWeeks);
        Assert.Equal((460, 461), (status.Week, status.CurrentWeek));
        Assert.Equal(Posted, status.SetPostedUtc);
        Assert.False(status.JudgingOpen);
        Assert.Equal(Utc("2026-11-27T08:00:00Z"), status.JudgingOpensUtc);
    }

    [Fact]
    public void A_site_two_weeks_behind_is_two_weeks_behind()
    {
        Assert.Equal((460, 462), (Status("2026-12-02T10:00:00Z").Week, Status("2026-12-02T10:00:00Z").CurrentWeek));
    }

    [Fact]
    public void A_set_kept_from_before_the_reset_turns_old_at_the_reset()
    {
        // Fetched while the site still marked it fresh, read again after the reset.
        Assert.True(Status("2026-11-24T07:59:00Z", fresh: true).SetIsThisWeeks);
        var after = Status("2026-11-24T08:00:00Z", fresh: true);
        Assert.False(after.SetIsThisWeeks);
        Assert.Equal(461, after.CurrentWeek);
    }

    [Fact]
    public void On_friday_the_hints_can_be_this_weeks_before_the_set_is()
    {
        // The site shows week 461's hints; its sets are still the ones posted for week 460.
        var status = Status("2026-11-27T09:00:00Z", week: 461);
        Assert.True(status.HintsAreThisWeeks);
        Assert.False(status.SetIsThisWeeks);
        Assert.Equal(461, status.CurrentWeek);
    }
}

public class FashionApprovalTests
{
    private static readonly FashionSet Set = new([new FashionPiece("neck", "Sphene Choker", null), new FashionPiece("feet", "Cotton Dress Shoes", "Soot Black")],
                                                 [("hands", "Charcoal Grey"), ("legs", "Charcoal Grey")], false);

    private static ReportStatus Status(bool open, bool thisWeeks, int week = 460, int current = 461) =>
        new(week, current, week == current, thisWeeks, null, open, new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 1, 8, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void A_set_ready_to_present_needs_no_approval()
    {
        Assert.Empty(FashionApproval.Reasons(Status(open: true, thisWeeks: true, current: 460), FashionApproval.SetLines(Set)));
    }

    [Fact]
    public void Before_friday_with_last_weeks_set_both_reasons_are_given_with_the_pieces_and_dyes()
    {
        Assert.Equal(
        [
            "Judging is closed until Fri 27 Nov 08:00 UTC: the outfit would be prepared, not presented.",
            "This outfit is for week 460, an earlier week. This week is week 461.",
            "Pieces: Sphene Choker (neck), Cotton Dress Shoes (feet, Soot Black).",
            "Dyes: hands Charcoal Grey, legs Charcoal Grey.",
        ], FashionApproval.Reasons(Status(open: false, thisWeeks: false), FashionApproval.SetLines(Set)));
    }

    [Fact]
    public void On_friday_before_the_site_posts_only_the_week_is_the_reason()
    {
        var reasons = FashionApproval.Reasons(Status(open: true, thisWeeks: false), FashionApproval.SetLines(Set));
        Assert.StartsWith("This outfit is for week 460", reasons[0]);
        Assert.DoesNotContain(reasons, r => r.StartsWith("Judging"));
    }

    [Fact]
    public void This_weeks_set_while_judging_is_closed_only_says_so()
    {
        var reason = Assert.Single(FashionApproval.Reasons(Status(open: false, thisWeeks: true, current: 460), FashionApproval.SetLines(Set)));
        Assert.StartsWith("Judging is closed", reason);
    }

    [Fact]
    public void With_this_weeks_hints_but_no_set_yet_it_says_so()
    {
        var reasons = FashionApproval.Reasons(Status(open: true, thisWeeks: false, week: 461, current: 461), FashionApproval.SetLines(Set));
        Assert.Equal("fashionreportxiv.com has this week's hints but has not posted this week's set yet: this one is from an earlier week.", reasons[0]);
    }
}
