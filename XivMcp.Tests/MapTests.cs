using System.Text.Json.Nodes;
using XivMcp.Maps;

namespace XivMcp.Tests;

public class MapTests
{
    // Most overworld maps: size factor 100, no offset. Cities and housing use other factors.
    private static readonly MapInfo Field = new(100, 0, 0);
    private static readonly MapInfo City = new(200, -64, 32);

    [Fact]
    public void The_world_centre_is_the_map_centre()
    {
        var (x, y) = MapMath.ToMap(Field, 0, 0);
        Assert.Equal(21.5, x, 3);
        Assert.Equal(21.5, y, 3);
    }

    [Theory]
    [InlineData(-512.0, 300.0)]
    [InlineData(12.5, -88.25)]
    public void Map_and_world_coordinates_convert_back_and_forth(double worldX, double worldZ)
    {
        foreach (var map in new[] { Field, City })
        {
            var (mx, my) = MapMath.ToMap(map, worldX, worldZ);
            var (wx, wz) = MapMath.ToWorld(map, mx, my);
            Assert.Equal(worldX, wx, 3);
            Assert.Equal(worldZ, wz, 3);
        }
    }

    [Fact]
    public void A_map_unit_is_about_fifty_yalms_on_a_field_map_and_half_that_in_a_city()
    {
        Assert.Equal(49.95, MapMath.YalmsPerMapUnit(Field), 2);
        Assert.Equal(24.98, MapMath.YalmsPerMapUnit(City), 2);
    }

    [Fact]
    public void Arrival_needs_the_same_zone_and_being_within_the_radius_on_the_ground()
    {
        Assert.True(MapMath.Arrived(132, 10, 10, 132, 13, 14, 5));     // 5 yalms away
        Assert.False(MapMath.Arrived(132, 10, 10, 132, 20, 10, 5));    // 10 yalms away
        Assert.False(MapMath.Arrived(133, 13, 14, 132, 13, 14, 5));    // another zone
    }

    // ---- routes

    private static IReadOnlyList<Waypoint> Three =>
    [
        new("Central Shroud", 21.5, 20.1, "Bentbranch", null),
        new(null, 25.0, 18.0, null, 8),
        new("East Shroud", 17.3, 27.0, "Hawthorne Hut", null),
    ];

    [Fact]
    public void A_route_flags_each_waypoint_waits_for_arrival_and_clears_the_flag_at_the_end()
    {
        var steps = RoutePlan.Steps(Three, 10, 60, true);
        Assert.Equal(["set_map_flag", "wait_until_arrived", "set_map_flag", "wait_until_arrived", "set_map_flag", "wait_until_arrived", "clear_map_flag"],
                     steps.Select(s => s.Tool));
        Assert.Equal(["flag1", "reach1", "flag2", "reach2", "flag3", "reach3", "done"], steps.Select(s => s.Id));
    }

    [Fact]
    public void Route_steps_carry_the_waypoint_its_radius_and_a_readable_note()
    {
        var steps = RoutePlan.Steps(Three, 10, 45, false);
        var flag = steps[0].Args;
        Assert.Equal("Central Shroud", flag["zone"]!.GetValue<string>());
        Assert.Equal(21.5, flag["x"]!.GetValue<double>());
        Assert.False(flag["open_map"]!.GetValue<bool>());
        Assert.Equal(10, steps[1].Args["radius"]!.GetValue<double>());
        Assert.Equal(45, steps[1].Args["timeout_minutes"]!.GetValue<int>());
        Assert.Equal(8, steps[3].Args["radius"]!.GetValue<double>());      // the waypoint's own radius
        Assert.False(steps[2].Args.ContainsKey("zone"));                    // no zone: the current one at that point
        Assert.Equal("Waypoint 1 of 3: Bentbranch", steps[0].Note);
        Assert.Equal("Waypoint 2 of 3", steps[2].Note);
    }

    [Theory]
    [InlineData(0.5, 10)]
    [InlineData(10, 43)]
    public void Waypoints_off_the_map_are_refused(double x, double y) =>
        Assert.Throws<ArgumentException>(() => RoutePlan.Steps([new Waypoint(null, x, y, null, null)], 10, 60, true));

    [Fact]
    public void A_route_needs_at_least_one_and_at_most_fifty_waypoints()
    {
        Assert.Throws<ArgumentException>(() => RoutePlan.Steps([], 10, 60, true));
        Assert.Throws<ArgumentException>(() => RoutePlan.Steps(Enumerable.Repeat(new Waypoint(null, 10, 10, null, null), 51).ToList(), 10, 60, true));
    }

    [Fact]
    public void Waypoints_are_read_from_json()
    {
        var w = RoutePlan.Parse(JsonNode.Parse("""[{ "zone": "Limsa Lominsa Lower Decks", "x": 9.5, "y": 11.2, "name": "Aftcastle", "radius": 6 }, { "x": 10, "y": 12 }]""")!);
        Assert.Equal(2, w.Count);
        Assert.Equal(new Waypoint("Limsa Lominsa Lower Decks", 9.5, 11.2, "Aftcastle", 6), w[0]);
        Assert.Null(w[1].Zone);
    }
}
