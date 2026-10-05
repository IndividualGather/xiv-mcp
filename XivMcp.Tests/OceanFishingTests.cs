using System.Collections.Generic;
using System.Linq;
using XivMcp.Integrations;
using XivMcp.Permissions;
using XivMcp.OceanFishing;

namespace XivMcp.Tests;

public class OceanScheduleTests
{
    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso + "Z");

    [Theory]
    [InlineData("2026-10-05T20:00:00", true)]
    [InlineData("2026-10-05T20:14:59", true)]
    [InlineData("2026-10-05T20:15:00", false)]
    [InlineData("2026-10-05T21:05:00", false)] // odd hours never board
    [InlineData("2026-10-05T19:59:59", false)]
    public void Boarding_is_open_for_15_minutes_from_every_even_UTC_hour(string at, bool open)
    {
        Assert.Equal(open, OceanSchedule.BoardingOpen(Utc(at)));
    }

    [Theory]
    [InlineData("1970-01-01T16:00:00", 0)]
    [InlineData("1970-01-01T18:10:00", 1)]
    [InlineData("1970-01-13T16:00:00", 0)] // 144 slots of 2 hours = 12 days
    [InlineData("1970-01-01T14:00:00", 143)]
    public void The_route_table_row_advances_every_two_hours_from_the_epoch(string at, int row)
    {
        Assert.Equal(row, OceanSchedule.Row(Utc(at), 144));
    }

    [Fact]
    public void Upcoming_voyages_start_with_the_open_one()
    {
        var voyages = OceanSchedule.Upcoming(Utc("2026-10-05T20:05:00"), 3, 144);
        Assert.Equal([Utc("2026-10-05T20:00:00"), Utc("2026-10-05T22:00:00"), Utc("2026-10-06T00:00:00")], voyages.Select(v => v.Boarding));
        Assert.True(voyages[0].Open);
        Assert.False(voyages[1].Open);
        Assert.Equal(OceanSchedule.Row(Utc("2026-10-05T22:00:00"), 144), voyages[1].Row);
    }

    [Fact]
    public void Between_windows_the_next_even_hour_comes_first()
    {
        var next = OceanSchedule.Upcoming(Utc("2026-10-05T18:53:00"), 1, 144)[0];
        Assert.Equal(Utc("2026-10-05T20:00:00"), next.Boarding);
        Assert.Equal(Utc("2026-10-05T20:15:00"), next.Departure);
        Assert.False(next.Open);
    }
}

public class OceanFishDataTests
{
    // The shape of Distant Seas' Data/indigo.json, cut down.
    private const string Json = """
        [
          { "Type": "GaladionBay", "IsSpectral": false, "Fish": [
            { "ItemId": 1, "CellType": "None", "CanCauseSpectral": false, "AveragePoints": 10, "Stars": 1,
              "BiteTimes": { "29714": { "CellType": "BestOrRequired" }, "29715": { "CellType": "Usable" } },
              "TimeAvailability": {}, "Intuition": null },
            { "ItemId": 2, "CellType": "None", "CanCauseSpectral": true, "AveragePoints": 40, "Stars": 2,
              "BiteTimes": { "29715": { "CellType": "BestOrRequired" } },
              "TimeAvailability": {}, "Intuition": null }
          ] },
          { "Type": "GaladionBay", "IsSpectral": true, "Fish": [
            { "ItemId": 3, "CellType": "None", "CanCauseSpectral": false, "AveragePoints": 300, "Stars": 4,
              "BiteTimes": { "29716": { "CellType": "BestOrRequired" } },
              "TimeAvailability": { "Day": false, "Sunset": true, "Night": false }, "Intuition": null },
            { "ItemId": 4, "CellType": "Intuition", "CanCauseSpectral": false, "AveragePoints": 200, "Stars": 3,
              "BiteTimes": {}, "TimeAvailability": {}, "Intuition": { "Duration": 60 } }
          ] },
          { "Type": "SomewhereNew", "IsSpectral": false, "Fish": [] }
        ]
        """;

    private static readonly IReadOnlyList<OceanSpotData> Spots = OceanFishData.Parse(Json);

    [Fact]
    public void Spots_map_to_the_game_s_IKDSpot_rows_and_unknown_ones_are_skipped()
    {
        Assert.Equal(2, Spots.Count);
        Assert.All(Spots, s => Assert.Equal(1u, s.SpotId));
        Assert.Equal([false, true], Spots.Select(s => s.Spectral));
    }

    [Fact]
    public void Each_fish_has_its_best_bait_points_and_flags()
    {
        var fish = Spots[0].Fish[1];
        Assert.Equal(2u, fish.ItemId);
        Assert.Equal(29715u, fish.BestBait);
        Assert.Equal(40, fish.Points);
        Assert.True(fish.TriggersSpectral);
        Assert.True(Spots[1].Fish[1].Intuition);
        Assert.Null(Spots[1].Fish[1].BestBait);
    }

    [Fact]
    public void Highlights_are_the_spectral_triggers_and_the_best_scorers_available_at_that_time()
    {
        var day = OceanFishData.Highlights(Spots, spotId: 1, OceanTime.Day, top: 2);
        Assert.Equal([2u], day.Normal.Where(f => f.TriggersSpectral).Select(f => f.ItemId));
        Assert.Equal([4u], day.Spectral.Select(f => f.ItemId)); // fish 3 only bites at sunset
        var sunset = OceanFishData.Highlights(Spots, spotId: 1, OceanTime.Sunset, top: 2);
        Assert.Equal([3u, 4u], sunset.Spectral.Select(f => f.ItemId));
    }
}

public class OceanTargetTests
{
    [Fact]
    public void Without_a_target_one_voyage_is_done()
    {
        var target = new OceanTarget();
        Assert.False(target.Reached(new OceanProgress(0, 0, new Dictionary<uint, int>())));
        Assert.True(target.Reached(new OceanProgress(1, 0, new Dictionary<uint, int>())));
    }

    [Fact]
    public void A_voyage_count_is_reached_after_that_many_voyages()
    {
        var target = new OceanTarget(Voyages: 3);
        Assert.False(target.Reached(new OceanProgress(2, 9999, new Dictionary<uint, int>())));
        Assert.True(target.Reached(new OceanProgress(3, 0, new Dictionary<uint, int>())));
    }

    [Fact]
    public void Points_add_up_over_voyages()
    {
        var target = new OceanTarget(Points: 5000);
        Assert.False(target.Reached(new OceanProgress(2, 4999, new Dictionary<uint, int>())));
        Assert.True(target.Reached(new OceanProgress(3, 5200, new Dictionary<uint, int>())));
    }

    [Fact]
    public void A_fish_target_counts_that_fish()
    {
        var target = new OceanTarget(Fish: 29788, FishCount: 2);
        Assert.False(target.Reached(new OceanProgress(5, 0, new Dictionary<uint, int> { [29788] = 1 })));
        Assert.True(target.Reached(new OceanProgress(6, 0, new Dictionary<uint, int> { [29788] = 2 })));
    }

    [Fact]
    public void Several_targets_stop_at_the_first_one_reached_and_max_voyages_caps_a_fish_hunt()
    {
        var target = new OceanTarget(Points: 100000, Fish: 29788, MaxVoyages: 4);
        Assert.True(target.Reached(new OceanProgress(1, 0, new Dictionary<uint, int> { [29788] = 1 })));
        Assert.True(target.Reached(new OceanProgress(4, 0, new Dictionary<uint, int>())));
        Assert.False(target.Reached(new OceanProgress(3, 0, new Dictionary<uint, int>())));
    }
}

public class OceanFishingModuleTests
{
    private static bool Only(string loaded, string id) => id == loaded;

    [Theory]
    [InlineData("get_ocean_fishing_schedule")]
    [InlineData("get_ocean_fishing_status")]
    [InlineData("board_ocean_fishing")]
    public void Reading_the_schedule_and_boarding_need_no_plugin(string tool)
    {
        Assert.True(IntegrationCatalog.IsAvailable(tool, _ => false));
    }

    [Theory]
    [InlineData("go_ocean_fishing")]
    [InlineData("fish_ocean_voyages")]
    public void Fishing_a_voyage_needs_AutoHook_whether_or_not_Distant_Seas_is_there(string tool)
    {
        Assert.False(IntegrationCatalog.IsAvailable(tool, _ => false));
        Assert.False(IntegrationCatalog.IsAvailable(tool, id => Only("DistantSeas", id)));
        Assert.True(IntegrationCatalog.IsAvailable(tool, id => Only("AutoHook", id)));
        var needs = ToolRequirements.For(tool);
        Assert.Contains(needs, r => r.PluginId == "AutoHook" && r.Need == Need.Needed);
        Assert.Contains(needs, r => r.PluginId == "DistantSeas" && r.Need == Need.Improves);
        Assert.DoesNotContain(needs, r => r.PluginId == "DistantSeas" && r.Need == Need.Needed);
    }

    [Fact]
    public void The_departure_alarm_is_Distant_Seas_own()
    {
        Assert.False(IntegrationCatalog.IsAvailable("set_ocean_fishing_alarm", id => Only("AutoHook", id)));
        Assert.True(IntegrationCatalog.IsAvailable("set_ocean_fishing_alarm", id => Only("DistantSeas", id)));
    }

    [Fact]
    public void The_module_is_called_ocean_fishing_and_shows_without_its_plugin()
    {
        var group = PermissionCatalog.Find("distantseas")!;
        Assert.Equal("Ocean fishing", group.Title);
        Assert.True(group.Standalone);
    }
}

public class RailingTests
{
    private static readonly System.Numerics.Vector3 Middle = new(0, 6.7f, -4);

    [Fact]
    public void Alone_on_the_boat_the_nearest_railing_spot_is_taken()
    {
        var spot = Railing.FreeSpot(new System.Numerics.Vector3(5, 6.7f, 0), []);
        Assert.Equal(7.1f, spot.Position.X);
        Assert.Equal(0f, spot.Position.Z);
        Assert.Equal(1.5f, spot.Facing);
    }

    [Fact]
    public void A_spot_keeps_its_distance_from_other_players()
    {
        var crowd = Enumerable.Range(0, 30).Select(i => new System.Numerics.Vector3(7.1f, 6.7f, -12 + i * 0.5f)).ToList();
        var spot = Railing.FreeSpot(Middle, crowd);
        Assert.Equal(-7.1f, spot.Position.X); // the port railing is full
        Assert.Equal(-1.5f, spot.Facing);
        Assert.True(spot.Gap >= 3);
    }

    [Fact]
    public void When_the_boat_is_full_the_spot_with_the_most_room_is_taken()
    {
        var players = new List<System.Numerics.Vector3>();
        foreach (var x in new[] { 7.1f, -7.1f })
            for (var z = -12f; z <= 3.5f; z += 1f) players.Add(new(x, 6.7f, z));
        var spot = Railing.FreeSpot(Middle, players);
        Assert.True(spot.Gap >= 0.5f - 0.01f);
        Assert.DoesNotContain(players, p => p.X == spot.Position.X && p.Z == spot.Position.Z);
    }
}

public class OceanPresetPageTests
{
    // The layout of AutoHook's wiki page "Ocean Fishing": a section per goal (and stop), each with export strings in code blocks.
    private const string Page = """
        # Ocean Fishing
        These presets aim to get you the maximum points.

        ### POINTS - Indigo - Galadion Bay
        ```
        AHFOLDER_aaa
        ```

        ### POINTS - Ruby - Kugane
        ```
        AHFOLDER_bbb
        ```

        ### ACHIEVES
        ```

        ```

        ### LEGENDS
        ```
        AH4_ccc
        ```

        ### LEVELING
        ```
        not a preset
        ```
        """;

    [Fact]
    public void Export_strings_are_grouped_by_the_goal_of_their_section()
    {
        var presets = OceanPresetPage.Parse(Page);
        Assert.Equal(["AHFOLDER_aaa", "AHFOLDER_bbb"], presets[OceanGoal.Points].Select(p => p.Export));
        Assert.Equal("POINTS - Ruby - Kugane", presets[OceanGoal.Points][1].Title);
        Assert.Equal(["AH4_ccc"], presets[OceanGoal.Legendary].Select(p => p.Export));
    }

    [Fact]
    public void Empty_sections_and_other_text_give_no_presets()
    {
        var presets = OceanPresetPage.Parse(Page);
        Assert.False(presets.ContainsKey(OceanGoal.Achievement));
        Assert.False(presets.ContainsKey(OceanGoal.Levelling));
    }

    [Theory]
    [InlineData("points", OceanGoal.Points)]
    [InlineData("Legends", OceanGoal.Legendary)]
    [InlineData("legendary", OceanGoal.Legendary)]
    [InlineData("achievements", OceanGoal.Achievement)]
    [InlineData("leveling", OceanGoal.Levelling)]
    public void Goals_are_read_the_way_people_write_them(string text, OceanGoal goal)
    {
        Assert.Equal(goal, OceanPresetPage.Goal(text));
    }

    [Fact]
    public void Each_goal_has_its_own_folder_in_AutoHook()
    {
        Assert.Equal("XIV MCP Ocean - Points", OceanPresetPage.FolderName(OceanGoal.Points));
    }
}
