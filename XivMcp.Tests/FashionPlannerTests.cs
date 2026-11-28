using XivMcp.Fashion;

namespace XivMcp.Tests;

/// <summary>Planning the cheapest outfit for a score, with week 460 (hints on head, body, feet and neck; Charcoal Grey on hands and legs).</summary>
public class FashionPlannerTests
{
    private const uint Charcoal = 5, Black = 6;
    private const int Head = GearSlots.Head, Body = GearSlots.Body, Hands = GearSlots.Hands, Legs = GearSlots.Legs, Feet = GearSlots.Feet,
                      Neck = GearSlots.Neck, Weapon = GearSlots.MainHand;

    private static readonly int[] Hinted = [Head, Body, Feet, Neck];

    private static FashionCandidate Worn(uint id, bool matches = false, byte stain = 0, int channels = 1, byte stain2 = 0) =>
        new(id, $"worn {id}", PieceSource.Worn, matches, stain, channels, Stain2: stain2);

    private static FashionCandidate Has(uint id, PieceSource source = PieceSource.Carried, bool matches = false, byte? stain = 0, int channels = 1,
                                        long? price = null, int copies = 1) =>
        new(id, $"item {id}", source, matches, stain, channels, price, Copies: copies);

    /// <summary>Every judged slot wearing a plain piece, with what a test adds or replaces.</summary>
    private static List<FashionSlot> Outfit(Dictionary<int, FashionCandidate[]>? overrides = null, Dictionary<int, uint>? dyes = null)
    {
        dyes ??= new() { [Hands] = Charcoal, [Legs] = Charcoal };
        return FashionScore.Judged.Select(slot => new FashionSlot(slot, Hinted.Contains(slot), dyes.TryGetValue(slot, out var d) ? d : null,
            overrides?.TryGetValue(slot, out var c) == true ? c : [Worn((uint)(100 + slot))])).ToList();
    }

    private static FashionOutfit Plan(List<FashionSlot> slots, int target = 80, bool allowReplace = true) =>
        FashionPlanner.Plan(slots, target, _ => FashionPlanner.DyeOwned, allowReplace);

    private static FashionChoice In(FashionOutfit o, int slot) => o.Choices.Single(c => c.Slot == slot);

    [Fact]
    public void Wearing_plain_pieces_everywhere_scores_68_plus_2_for_a_hinted_accessory()
    {
        Assert.Equal(70, FashionScore.Judged.Sum(s => FashionScore.Piece(s, Hinted.Contains(s), true, false)));
        Assert.Equal(68, FashionScore.Judged.Sum(s => FashionScore.Piece(s, s is Head or Body or Feet or Legs, true, false)));
    }

    [Fact]
    public void Week_460s_easy_80_is_the_necklace_and_both_dyes()
    {
        var outfit = Plan(Outfit(new() { [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)] }));
        Assert.True(outfit.ReachesTarget);
        Assert.Equal(80, outfit.Score);
        Assert.Equal(4326u, In(outfit, Neck).Piece!.ItemId);
        Assert.Equal(DyeChange.Dye, In(outfit, Hands).Dye);
        Assert.Equal(DyeChange.Dye, In(outfit, Legs).Dye);
    }

    [Fact]
    public void A_matching_piece_the_player_has_beats_buying_the_sites_pick()
    {
        var outfit = Plan(Outfit(new() { [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300), Has(5000, PieceSource.Retainer, matches: true)] }));
        Assert.Equal(5000u, In(outfit, Neck).Piece!.ItemId);
    }

    [Fact]
    public void Nothing_is_changed_that_the_score_does_not_need()
    {
        // A matching necklace and a matching head piece in the bags: 70 + 6 + 8 = 84, so no dye is needed for 80.
        var outfit = Plan(Outfit(new()
        {
            [Neck] = [Worn(109), Has(5000, matches: true)],
            [Head] = [Worn(102), Has(5001, matches: true)],
        }));
        Assert.Equal(84, outfit.Score);
        Assert.All(outfit.Choices, c => Assert.Equal(DyeChange.None, c.Dye));
    }

    [Fact]
    public void A_piece_already_in_the_weeks_color_scores_without_a_dye()
    {
        var outfit = Plan(Outfit(new()
        {
            [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)],
            [Hands] = [Worn(104, stain: (byte)Charcoal)],
            [Legs] = [Worn(106), Has(5002, stain: (byte)Charcoal)],
        }));
        Assert.Equal(DyeChange.AlreadyRight, In(outfit, Hands).Dye);
        Assert.Equal((5002u, DyeChange.AlreadyRight), (In(outfit, Legs).Piece!.ItemId, In(outfit, Legs).Dye));
    }

    [Fact]
    public void An_empty_slot_gets_a_piece_since_it_scores_nothing()
    {
        // Earrings missing: 70 - 8 = 62 + 6 + 2 + 2 = 72, short of 80 until something goes in the ears.
        var outfit = Plan(Outfit(new()
        {
            [GearSlots.Ears] = [Has(7000), Has(7001, PieceSource.Vendor, price: 50)],
            [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)],
        }));
        Assert.Equal(7000u, In(outfit, GearSlots.Ears).Piece!.ItemId);
        Assert.Equal(80, outfit.Score);
    }

    [Fact]
    public void A_dye_the_player_put_on_is_kept_when_another_piece_can_take_the_color()
    {
        var outfit = Plan(Outfit(new()
        {
            [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)],
            [Hands] = [Worn(104, stain: (byte)Black), Has(5003, stain: 0)],
        }));
        Assert.Equal((5003u, DyeChange.Dye), (In(outfit, Hands).Piece!.ItemId, In(outfit, Hands).Dye));
    }

    [Fact]
    public void An_existing_dye_is_replaced_only_as_the_last_way_and_never_without_approval()
    {
        var slots = Outfit(new()
        {
            [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)],
            [Hands] = [Worn(104, stain: (byte)Black)],
        });
        Assert.Equal(DyeChange.Replace, In(Plan(slots), Hands).Dye);
        var without = Plan(slots, allowReplace: false);
        Assert.False(without.ReachesTarget);
        Assert.Equal(78, without.Score);
    }

    [Fact]
    public void A_piece_that_takes_no_dye_gets_none()
    {
        var outfit = Plan(Outfit(new()
        {
            [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)],
            [Hands] = [Worn(104, channels: 0), Has(7002, PieceSource.Vendor, price: 80)],
        }));
        Assert.Equal((7002u, DyeChange.Dye), (In(outfit, Hands).Piece!.ItemId, In(outfit, Hands).Dye));
    }

    [Fact]
    public void A_second_dye_channel_already_in_the_color_counts()
    {
        var outfit = Plan(Outfit(new()
        {
            [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)],
            [Hands] = [Worn(104, stain: (byte)Black, channels: 2, stain2: (byte)Charcoal)],
        }));
        Assert.Equal(DyeChange.AlreadyRight, In(outfit, Hands).Dye);
    }

    [Fact]
    public void An_empty_second_channel_takes_the_color_instead_of_replacing_the_first()
    {
        var outfit = Plan(Outfit(new()
        {
            [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)],
            [Hands] = [Worn(104, stain: (byte)Black, channels: 2)],
        }));
        Assert.Equal((DyeChange.Dye, 2), (In(outfit, Hands).Dye, In(outfit, Hands).Channel));
    }

    [Fact]
    public void The_outfit_reads_as_lines_for_the_player()
    {
        var outfit = Plan(Outfit(new()
        {
            [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)],
            [Hands] = [Worn(104, stain: (byte)Black), Has(5003, PieceSource.Retainer, stain: 0) with { Retainer = "Kirche" }],
        }));
        Assert.Equal(
        [
            "Hands: item 5003 (from Kirche), dyed Charcoal Grey.",
            "Legs: worn 106 (worn), dyed Charcoal Grey.",
            "Neck: item 4326 (bought from a vendor, 300 gil).",
        ], FashionOutfitText.Lines(outfit, slot => slot is Hands or Legs ? "Charcoal Grey" : null));
    }

    [Fact]
    public void A_planned_outfit_needs_this_weeks_hints_but_not_the_sites_set()
    {
        var friday = new ReportStatus(461, 461, true, false, null, true, default, default);
        Assert.Empty(FashionApproval.Reasons(friday, ["Head: x."], setNeeded: false));
        Assert.NotEmpty(FashionApproval.Reasons(friday, ["Head: x."]));
        var wednesday = new ReportStatus(460, 461, false, false, null, false, new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc), default);
        Assert.Equal(
        [
            "Judging is closed until Fri 27 Nov 08:00 UTC: the outfit would be prepared, not presented.",
            "This outfit is for week 460, an earlier week. This week is week 461.",
            "Head: x.",
        ], FashionApproval.Reasons(wednesday, ["Head: x."], setNeeded: false));
    }

    [Fact]
    public void A_piece_whose_dye_is_unknown_is_not_dyed()
    {
        var options = FashionPlanner.Options(new FashionSlot(Hands, false, Charcoal, [Has(5004, PieceSource.Retainer, stain: null)]), _ => 2, true);
        Assert.DoesNotContain(options, o => o.Dye != DyeChange.None && o.Piece is not null);
    }

    [Fact]
    public void A_trip_to_a_retainer_beats_buying_and_cheaper_buys_win()
    {
        var neck = new[] { Worn(109), Has(1, PieceSource.Market, matches: true, price: 900), Has(2, PieceSource.Vendor, matches: true, price: 300) };
        Assert.Equal(2u, In(Plan(Outfit(new() { [Neck] = neck })), Neck).Piece!.ItemId);
        Assert.Equal(3u, In(Plan(Outfit(new() { [Neck] = [.. neck, Has(3, PieceSource.Retainer, matches: true)] })), Neck).Piece!.ItemId);
    }

    [Fact]
    public void A_ring_the_player_has_once_is_not_planned_on_both_hands()
    {
        var slots = FashionScore.Judged.Select(slot => slot switch
        {
            GearSlots.RingRight or GearSlots.RingLeft => new FashionSlot(slot, false, null, [Has(8000), Has(8001, PieceSource.Vendor, price: 40)]),
            _ => new FashionSlot(slot, false, null, [Worn((uint)(100 + slot))]),
        }).ToList();
        var outfit = FashionPlanner.Plan(slots, 100, _ => 0);
        Assert.Equal(100, outfit.Score);
        Assert.NotEqual(In(outfit, GearSlots.RingRight).Piece!.ItemId, In(outfit, GearSlots.RingLeft).Piece!.ItemId);
    }

    [Fact]
    public void A_left_ring_hint_gets_its_match_on_the_left_hand_only()
    {
        // The right ring keeps what is worn; the matching ring the player has once goes on the left.
        var slots = FashionScore.Judged.Select(slot => slot switch
        {
            GearSlots.RingLeft => new FashionSlot(slot, true, null, [Worn(112), Has(9000, matches: true)]),
            GearSlots.RingRight => new FashionSlot(slot, false, null, [Worn(111), Has(9000)]),
            _ => new FashionSlot(slot, false, null, [Worn((uint)(100 + slot))]),
        }).ToList();
        var outfit = FashionPlanner.Plan(slots, 100, _ => 0);
        Assert.Equal(100, outfit.Score);
        Assert.Equal(9000u, In(outfit, GearSlots.RingLeft).Piece!.ItemId);
        Assert.Equal(PieceSource.Worn, In(outfit, GearSlots.RingRight).Piece!.Source);
    }

    [Fact]
    public void Without_a_way_to_the_score_the_best_outfit_says_how_far_it_gets()
    {
        var outfit = Plan(Outfit(), target: 100);
        Assert.False(outfit.ReachesTarget);
        Assert.Equal(74, outfit.Score); // 70 and both dyes
    }

    [Fact]
    public void The_weapon_takes_the_weeks_dye_only_when_it_has_none()
    {
        var slots = Outfit(new() { [Weapon] = [Worn(100)], [Neck] = [Worn(109), Has(4326, PieceSource.Vendor, matches: true, price: 300)] },
                           new() { [Weapon] = Charcoal });
        Assert.Equal(DyeChange.Dye, In(Plan(slots, 78), Weapon).Dye);
    }
}

public class FashionTargetTests
{
    [Theory]
    [InlineData(null, null, 80)]
    [InlineData(100, null, 100)]
    [InlineData(80, "easy100", 80)] // 'target' wins
    [InlineData(null, "easy100", 100)]
    [InlineData(null, "easy80", 80)]
    public void The_score_comes_from_target_or_the_older_set(int? target, string? set, int score)
    {
        Assert.Equal(score, FashionTarget.Of(target, set));
    }

    [Fact]
    public void Other_scores_are_refused()
    {
        Assert.Throws<ArgumentException>(() => FashionTarget.Of(90, null));
        Assert.Throws<ArgumentException>(() => FashionTarget.Of(null, "easy90"));
    }

    [Fact]
    public void The_outfit_costs_what_its_bought_pieces_do()
    {
        var outfit = new FashionOutfit(
        [
            new FashionChoice(GearSlots.Head, new FashionCandidate(1, "a", PieceSource.Vendor, true, 0, 1, Price: 300), DyeChange.None, 10, 0),
            new FashionChoice(GearSlots.Body, new FashionCandidate(2, "b", PieceSource.Market, true, 0, 1, Price: 47_997), DyeChange.Dye, 12, 0),
            new FashionChoice(GearSlots.Hands, new FashionCandidate(3, "c", PieceSource.Retainer, false, 0, 1), DyeChange.None, 10, 0),
            new FashionChoice(GearSlots.Ears, null, DyeChange.None, 0, 0),
        ], 32, false);
        Assert.Equal(48_297, FashionOutfitText.PiecesGil(outfit));
        Assert.Matches(@"^About 48\D?297 gil in all\.$", FashionOutfitText.CostLine(48_297)); // the thousands separator follows the game's culture
        Assert.Null(FashionOutfitText.CostLine(0));
    }
}

public class FashionSiteDataTests
{
    [Fact]
    public void A_hint_list_gives_its_item_names()
    {
        const string json = """{"found":true,"hint":"Jewels of Yellow","slot":"neck","items":[{"name":"Sphene Choker"},{"name":"Heliodor Choker"},{"name":"Sphene Choker"}]}""";
        Assert.Equal(["Sphene Choker", "Heliodor Choker"], WeeklyReport.HintItems(json));
        Assert.Empty(WeeklyReport.HintItems("""{"found":false}"""));
    }

    [Fact]
    public void The_weeks_dyes_come_from_the_sites_dye_data_before_the_sets()
    {
        const string json = """
            {"lastOptions":{"week":"460","reportTitle":"x","hints":[]},
             "dyeData":{"head":{"plus1":"grey","plus2":"Charcoal Grey"},"feet":{"plus1":"black","plus2":"Gunmetal Black Dye"},"_updatedAt":1},
             "easy80":{"itemPairs":[{"slot":"neck","name":"Sphene Choker"}],"dyes":{"hands":"Charcoal Grey"}},"dyesFresh":true}
            """;
        var report = WeeklyReport.Parse(json);
        Assert.True(report.DyesFresh);
        Assert.Empty(report.WantedDyes(report.Easy80, known: false));
        Assert.Equal(new Dictionary<string, string> { ["head"] = "Charcoal Grey", ["feet"] = "Gunmetal Black" }, report.WantedDyes(report.Easy80));
        Assert.Equal(new Dictionary<string, string> { ["hands"] = "Charcoal Grey" }, (report with { Dyes = null }).WantedDyes(report.Easy80));
    }
}
