using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using XivMcp.Fishing;

namespace XivMcp.Tests;

public class FishingDataTests
{
    // The shape of the fish tracker's data.js, shortened.
    internal const string TrackerJs = """
        const DATA = {
          FISH: {
            "4905": {"_id": 4905, "previousWeatherSet": [3], "weatherSet": [7, 15], "startHour": 17.5, "endHour": 6, "location": 90,
                     "bestCatchPath": [2601, 4869], "predators": [[5031, 3]], "intuitionLength": 120, "patch": 2, "folklore": 2500,
                     "collectable": 1, "fishEyes": true, "bigFish": true, "snagging": null, "lure": "Ambitious", "hookset": "Powerful",
                     "tug": "heavy", "gig": null, "aquarium": null, "dataMissing": null}
          },
          FISHING_SPOTS: { "90": {"_id": 90, "name_en": "Lake {test}"} },
          FOLKLORE: { "2500": {"_id": 2500, "name_en": "Tome of Ichthyological Folklore - Dravania"} }
        };
        """;

    [Fact]
    public void Reads_the_fish_trackers_data()
    {
        var f = FishTrackerData.ParseFish(TrackerJs)[4905];
        Assert.Equal(90u, f.Spot);
        Assert.Equal(new FishConditions(17.5, 6, [3], [7, 15]), f.Conditions with { }, new ConditionsComparer());
        Assert.Equal([2601u, 4869u], f.CatchPath);
        Assert.Equal((5031u, 3), Assert.Single(f.Predators));
        Assert.Equal(120, f.IntuitionSeconds);
        Assert.Equal(2500u, f.Folklore);
        Assert.True(f.FishEyes && f.BigFish && f.Collectable);
        Assert.Null(f.Snagging);
        Assert.Equal("Ambitious", f.Lure);
        Assert.Equal(Hookset.Powerful, f.Hookset);
        Assert.Equal(Tug.Heavy, f.Tug);
    }

    [Fact]
    public void Reads_folklore_names_and_skips_braces_in_strings()
    {
        Assert.Equal("Tome of Ichthyological Folklore - Dravania", FishTrackerData.ParseFolklore(TrackerJs)[2500]);
    }

    [Fact]
    public void Reads_teamcrafts_sources_and_spots()
    {
        var sources = TeamcraftFishData.ParseSources("""{ "4869": [ { "spot": 90, "hookset": 2, "tug": 2, "bait": 2601, "snagging": false, "minGathering": 0 } ] }""");
        Assert.Equal(new FishSource(90, 2601, Tug.Light, Hookset.Precision, false, 0), Assert.Single(sources[4869]));
        var spot = TeamcraftFishData.ParseSpots("""[ { "id": 90, "level": 50, "coords": { "x": 22.91, "y": 22.19 }, "fishes": [4869, 4905] } ]""")[90];
        Assert.Equal(new FishingSpotInfo(90, 22.91, 22.19, 50, spot.Fish), spot);
        Assert.Equal([4869u, 4905u], spot.Fish);
    }

    private sealed class ConditionsComparer : IEqualityComparer<FishConditions>
    {
        public bool Equals(FishConditions? a, FishConditions? b) =>
            a!.StartHour == b!.StartHour && a.EndHour == b.EndHour && a.PreviousWeathers.SequenceEqual(b.PreviousWeathers) && a.Weathers.SequenceEqual(b.Weathers);

        public int GetHashCode(FishConditions c) => 0;
    }
}

public class FishGuideTests
{
    private const uint Bait = 2601, Mooch = 4869, Target = 4905, Rival = 4870, Spot = 90;

    private static readonly IReadOnlyDictionary<uint, IReadOnlyList<FishSource>> Sources = new Dictionary<uint, IReadOnlyList<FishSource>>
    {
        [Mooch] = [new(Spot, Bait, Tug.Light, Hookset.Precision, false, 0)],
        [Target] = [new(Spot, Mooch, Tug.Heavy, Hookset.Powerful, false, 0)],
        [Rival] = [new(Spot, Mooch, Tug.Heavy, Hookset.Powerful, false, 0)],
    };

    private static readonly IReadOnlyDictionary<uint, FishingSpotInfo> SpotFish = new Dictionary<uint, FishingSpotInfo>
    {
        [Spot] = new(Spot, 20, 20, 50, [Mooch, Target, Rival]),
    };

    private static BiteWindow? Bites(uint fish, uint spot, uint bait) => fish switch
    {
        Target => new BiteWindow(20, 25, 22, 300),
        Rival => new BiteWindow(8, 14, 10, 500),
        Mooch => new BiteWindow(5, 9, 7, 900),
        _ => null,
    };

    private static string Name(uint id) => id switch { Bait => "Bait Bug", Mooch => "Minnow", Target => "Big One", Rival => "Other One", 7 => "Rain", 3 => "Clouds", _ => $"#{id}" };

    private static FishGuide Guide(bool tracked = true) =>
        FishGuides.Build(Target, null, tracked ? FishTrackerData.ParseFish(FishingDataTests.TrackerJs) : new Dictionary<uint, TrackerFish>(), Sources, SpotFish, Bites)!;

    [Fact]
    public void Follows_the_bait_and_the_mooches_to_the_fish()
    {
        var g = Guide();
        Assert.Equal(Spot, g.Spot);
        Assert.Equal([Bait, Mooch], g.Path.Select(s => s.Bait));
        Assert.Equal([Mooch, Target], g.Path.Select(s => s.Fish));
        Assert.False(g.Path[0].Mooch);
        Assert.True(g.Path[1].Mooch);
        Assert.Equal(Tug.Light, g.Path[0].Tug);
        Assert.Equal(Tug.Heavy, g.Path[1].Tug);
        Assert.Equal(22, g.Last.Bite!.Median);
    }

    [Fact]
    public void Fish_the_tracker_does_not_know_follow_teamcraft()
    {
        var g = Guide(tracked: false);
        Assert.Equal([Bait, Mooch], g.Path.Select(s => s.Bait));
        Assert.True(g.Conditions.AnyTime);
        Assert.False(g.FromTracker);
    }

    [Fact]
    public void Knows_the_other_fish_on_the_same_bait()
    {
        var r = Assert.Single(Guide().Rivals);
        Assert.Equal(Rival, r.Fish);
        Assert.Equal(Tug.Heavy, r.Tug);
    }

    [Fact]
    public void Knows_how_the_fish_for_fishers_intuition_are_caught()
    {
        var sources = new Dictionary<uint, IReadOnlyList<FishSource>>(Sources) { [5031] = [new(Spot, Bait, Tug.Medium, Hookset.Powerful, false, 0)] };
        var g = FishGuides.Build(Target, null, FishTrackerData.ParseFish(FishingDataTests.TrackerJs), sources, SpotFish, Bites)!;
        var p = Assert.Single(g.PredatorCasts);
        Assert.Equal((Bait, 5031u, Tug.Medium), (p.Bait, p.Fish, p.Tug));
        Assert.False(p.Mooch);
        Assert.Contains(FishingAdvice.For(g, Name, Name).Steps, st => st == "Cast with Bait Bug and hook the medium (!!) bite with Powerful Hookset to catch #5031.");
    }

    [Fact]
    public void Unknown_fish_have_no_guide()
    {
        Assert.Null(FishGuides.Build(1, null, new Dictionary<uint, TrackerFish>(), Sources, SpotFish, Bites));
    }

    [Fact]
    public void Advice_names_every_cast_the_conditions_and_the_abilities()
    {
        var a = FishingAdvice.For(Guide(), Name, Name, _ => "Dravania's tome");
        var text = string.Join("\n", a.Steps.Concat(a.Abilities));
        Assert.Contains("Dravania's tome", text);
        Assert.Contains("Ambitious Lure", text);
        Assert.Contains("from 17:30 to 06:00 Eorzean time, in Rain or #15 after Clouds", text);
        Assert.Contains("3 #5031", text);
        Assert.Contains("Cast with Bait Bug and hook the light (!) bite with Precision Hookset to catch Minnow", text);
        Assert.Contains("Mooch with the Minnow you just caught and hook the heavy (!!!) bite with Powerful Hookset to catch Big One. It bites after about 20–25 s", text);
        Assert.Contains("Patience II", text);
        Assert.Contains("Collect", text);
        Assert.Contains("Fish Eyes", text);
    }

    [Fact]
    public void Rivals_with_the_same_tug_are_told_apart_by_time()
    {
        var a = FishingAdvice.For(Guide(), Name, Name);
        Assert.Contains(a.Steps, s => s.Contains("Other One (8–14 s)"));
        Assert.Contains(a.Steps, s => s.Contains("between 20–25 s"));
        Assert.Contains(a.Abilities, s => s.StartsWith("Surface Slap after catching Other One"));
        Assert.NotNull(a.CountingMacro);
    }

    [Fact]
    public void The_counting_macro_fits_the_games_limits()
    {
        var lines = FishingAdvice.CountingMacro(25).Split('\n');
        Assert.Equal("/ac Cast <wait.1>", lines[0]);
        Assert.True(lines.Length <= 15);
        Assert.StartsWith("/echo 1 s", lines[1]);
        Assert.All(lines, l => Assert.True(l.Length <= 180));
    }

    [Fact]
    public void Fish_without_conditions_need_no_when()
    {
        Assert.Null(FishingAdvice.When(FishConditions.Always, Name));
    }
}

public class AutoHookPresetTests
{
    private static FishGuide Guide() => new(
        4905, 90,
        [
            new CatchStep(2601, 4869, Tug.Light, Hookset.Precision, new BiteWindow(5, 9, 7, 900)),
            new CatchStep(4869, 4905, Tug.Heavy, Hookset.Powerful, new BiteWindow(20, 25, 22, 300)) { Mooch = true },
        ],
        FishConditions.Always, [], null, null, false, BigFish: true, Snagging: false, null, Collectable: false, [], true);

    [Fact]
    public void Hooks_only_the_tug_of_the_fish_each_cast_is_for()
    {
        var p = AutoHookPreset.Build(Guide(), "XIV MCP: Big One");
        var bait = p["ListOfBaits"]!.AsArray().Single()!;
        Assert.Equal(2601, (int)bait["BaitFish"]!["Id"]!);
        var normal = bait["NormalHook"]!;
        Assert.True((bool)normal["PatienceWeak"]!["HooksetEnabled"]!);
        Assert.Equal(4179u, (uint)normal["PatienceWeak"]!["HooksetType"]!);
        Assert.False((bool)normal["PatienceStrong"]!["HooksetEnabled"]!);
        Assert.False((bool)normal["PatienceLegendary"]!["HooksetEnabled"]!);
        Assert.Null(normal["TimeoutMax"]);

        var mooch = p["ListOfMooch"]!.AsArray().Single()!;
        Assert.Equal(4869, (int)mooch["BaitFish"]!["Id"]!);
        Assert.True((bool)mooch["NormalHook"]!["PatienceLegendary"]!["HooksetEnabled"]!);
        Assert.Equal(4103u, (uint)mooch["NormalHook"]!["PatienceLegendary"]!["HooksetType"]!);
        Assert.Equal(26.0, (double)mooch["NormalHook"]!["TimeoutMax"]!);
        Assert.Equal(568u, (uint)mooch["IntuitionHook"]!["RequiredStatus"]!);
    }

    [Fact]
    public void Mooches_the_fish_on_the_way_and_casts_on_its_own()
    {
        var p = AutoHookPreset.Build(Guide(), "XIV MCP: Big One");
        var fish = p["ListOfFish"]!.AsArray();
        Assert.True((bool)fish.Single(f => (int)f!["Fish"]!["Id"]! == 4869)!["Mooch"]!["Enabled"]!);
        Assert.Null(fish.Single(f => (int)f!["Fish"]!["Id"]! == 4905)!["Mooch"]);
        Assert.True((bool)p["AutoCastsCfg"]!["CastLine"]!["Enabled"]!);
        Assert.True((bool)p["AutoCastsCfg"]!["CastPatience"]!["Enabled"]!);
        Assert.Equal(2601, (int)p["ExtraCfg"]!["ForcedBaitId"]!);
    }

    private static FishGuide IntuitionGuide(uint predatorBait = 2601) => new FishGuide(
        24994, 168,
        [new CatchStep(2601, 24994, Tug.Heavy, Hookset.Powerful, new BiteWindow(25, 30, 27, 50))],
        FishConditions.Always, [(24203u, 3), (23056u, 1)], 175, null, false, BigFish: true, Snagging: false, "Ambitious", Collectable: false, [], true)
    {
        PredatorCasts =
        [
            new CatchStep(predatorBait, 24203, Tug.Light, Hookset.Precision, null),
            new CatchStep(predatorBait, 23056, Tug.Medium, Hookset.Powerful, null),
        ],
    };

    [Fact]
    public void Catches_the_fish_for_fishers_intuition_first_on_the_same_bait()
    {
        var g = IntuitionGuide();
        Assert.True(AutoHookPreset.CatchesPredators(g));
        var bait = AutoHookPreset.Build(g, "x")["ListOfBaits"]!.AsArray().Single()!;
        var normal = bait["NormalHook"]!;
        Assert.True((bool)normal["PatienceWeak"]!["HooksetEnabled"]!);
        Assert.True((bool)normal["PatienceStrong"]!["HooksetEnabled"]!);
        Assert.False((bool)normal["PatienceLegendary"]!["HooksetEnabled"]!);
        var intuition = bait["IntuitionHook"]!;
        Assert.True((bool)intuition["UseCustomStatusHook"]!);
        Assert.False((bool)intuition["PatienceWeak"]!["HooksetEnabled"]!);
        Assert.True((bool)intuition["PatienceLegendary"]!["HooksetEnabled"]!);

        var fish = AutoHookPreset.Build(g, "x")["ListOfFish"]!.AsArray();
        Assert.True((bool)fish.Single(f => (int)f!["Fish"]!["Id"]! == 24203)!["IdenticalCast"]!["Enabled"]!);
        Assert.Null(fish.Single(f => (int)f!["Fish"]!["Id"]! == 23056)!["IdenticalCast"]);
    }

    [Fact]
    public void Leaves_fish_for_fishers_intuition_on_other_baits_to_the_player()
    {
        var g = IntuitionGuide(predatorBait: 12345);
        Assert.False(AutoHookPreset.CatchesPredators(g));
        var normal = AutoHookPreset.Build(g, "x")["ListOfBaits"]!.AsArray().Single()!["NormalHook"]!;
        Assert.True((bool)normal["PatienceLegendary"]!["HooksetEnabled"]!);
        Assert.False((bool)normal["PatienceWeak"]!["HooksetEnabled"]!);
    }

    [Fact]
    public void Casts_the_lure_the_fish_needs()
    {
        var hook = AutoHookPreset.Build(IntuitionGuide(), "x")["ListOfBaits"]!.AsArray().Single()!["IntuitionHook"]!;
        var lures = hook["CastLures"]!;
        Assert.True((bool)lures["Enabled"]!);
        Assert.Equal(37594u, (uint)lures["Id"]!);
        Assert.True((bool)lures["Ambitious"]!["Special"]!["Enabled"]!);
        Assert.Null(AutoHookPreset.Build(Guide(), "x")["ListOfBaits"]!.AsArray().Single()!["NormalHook"]!["CastLures"]);
    }

    [Fact]
    public void Exports_in_autohooks_format()
    {
        var p = AutoHookPreset.Build(Guide(), "XIV MCP: Big One");
        var export = AutoHookPreset.Export(p);
        Assert.StartsWith("AH11_", export);
        var back = JsonNode.Parse(AutoHookPreset.Decode(export))!;
        Assert.Equal("XIV MCP: Big One", (string)back["PresetName"]!);
    }
}

public class TeamcraftBiteTimeTests
{
    [Fact]
    public void Asks_for_every_fish_at_a_spot_with_a_bait()
    {
        var q = JsonNode.Parse(TeamcraftBiteTimes.Query(90, 2601))!["query"]!.GetValue<string>();
        Assert.Contains("bite_time_per_fish_per_spot_per_bait", q);
        Assert.Contains("spot: {_eq: 90}", q);
        Assert.Contains("baitId: {_eq: 2601}", q);
    }

    [Fact]
    public void Turns_the_reports_into_a_window_per_fish()
    {
        var w = TeamcraftBiteTimes.Parse("""
            {"data":{"bite_time_per_fish_per_spot_per_bait":[
              {"itemId":4905,"spot":90,"baitId":2601,"flooredBiteTime":7,"occurences":2393},
              {"itemId":4905,"spot":90,"baitId":2601,"flooredBiteTime":6,"occurences":268},
              {"itemId":4870,"spot":90,"baitId":2601,"flooredBiteTime":12,"occurences":40}]}}
            """);
        Assert.Equal(6, w[4905].Min);
        Assert.Equal(8, w[4905].Max);
        Assert.Equal(12, w[4870].Min);
    }

    [Fact]
    public void Errors_and_odd_answers_give_nothing()
    {
        Assert.Empty(TeamcraftBiteTimes.Parse("""{"errors":[{"message":"nope"}]}"""));
        Assert.Empty(TeamcraftBiteTimes.Parse("not json"));
    }
}
