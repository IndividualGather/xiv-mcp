using System;
using System.Linq;
using XivMcp.Voyages;

namespace XivMcp.Tests;

public class SubmarineRouteTests
{
    private static readonly SubmarinePoint[] Points =
    [
        new(1, 1, "The Ivory Shoals", "A", 1, true),
        new(2, 1, "Deep-sea Site 1", "B", 1, true),
        new(3, 1, "Deep-sea Site 2", "C", 10, true),
        new(4, 1, "The Lightless Basin", "D", 20, false),
        new(32, 2, "The Ashen Trench", "A", 30, true),
    ];

    [Fact]
    public void Resolves_names_codes_and_ids_in_the_order_given()
    {
        var route = SubmarineRoutes.Resolve(["C", "deep-sea site 1", "1"], Points, vesselRank: 40);
        Assert.Equal([3u, 2u, 1u], route.Select(p => p.Id));
    }

    [Fact]
    public void Letters_are_read_on_the_sea_of_the_named_points()
    {
        Assert.Equal([2u, 1u], SubmarineRoutes.Resolve(["B", "A"], Points, 40, vesselSea: 1).Select(p => p.Id));
        Assert.Equal([2u, 1u], SubmarineRoutes.Resolve(["deep-sea site 1", "A"], Points, 40).Select(p => p.Id));
        Assert.Contains("No voyage point 'B'", Assert.Throws<FormatException>(() => SubmarineRoutes.Resolve(["The Ashen Trench", "B"], Points, 40)).Message);
    }

    [Fact]
    public void Names_match_with_any_apostrophe_the_letter_suffix_and_without_the()
    {
        SubmarinePoint[] points = [new(80, 4, "Crow’s Drop", "G", 95, true), new(81, 4, "the Anthemoessa Undertow", "I", 96, true)];
        Assert.Equal([80u], SubmarineRoutes.Resolve(["Crow's Drop"], points, 95).Select(p => p.Id));
        Assert.Equal([80u], SubmarineRoutes.Resolve(["crow's drop (g)"], points, 95).Select(p => p.Id));
        Assert.Equal([81u], SubmarineRoutes.Resolve(["Anthemoessa Undertow"], points, 96).Select(p => p.Id));
    }

    [Fact]
    public void Letters_alone_are_read_on_the_submersible_s_own_sea()
    {
        Assert.Equal([32u], SubmarineRoutes.Resolve(["A"], Points, 40, vesselSea: 2).Select(p => p.Id));
    }

    [Fact]
    public void A_letter_on_several_seas_is_refused_when_the_sea_is_unknown()
    {
        Assert.Contains("several seas", Assert.Throws<FormatException>(() => SubmarineRoutes.Resolve(["A"], Points, 40)).Message);
        Assert.Equal([2u], SubmarineRoutes.Resolve(["B"], Points, 40).Select(p => p.Id)); // only one sea has a B
    }

    [Theory]
    [InlineData(new[] { "Nowhere" }, "No voyage point")]
    [InlineData(new[] { "D" }, "not been unlocked")]
    [InlineData(new[] { "C" }, "rank 10")]
    [InlineData(new[] { "B", "B" }, "twice")]
    [InlineData(new[] { "A", "B", "C", "A", "B", "C" }, "at most 5")]
    [InlineData(new[] { "1", "32" }, "one sea")]
    public void Refuses_routes_the_game_does_not_allow(string[] wanted, string message)
    {
        var rank = message == "rank 10" ? 5 : 40;
        Assert.Contains(message, Assert.Throws<FormatException>(() => SubmarineRoutes.Resolve(wanted, Points, rank)).Message);
    }
}

public class VesselRepairTests
{
    [Fact]
    public void Condition_is_kept_in_hundredths_of_a_percent_times_three()
    {
        Assert.Equal(100, VesselRepair.Percent(30000));
        Assert.Equal(50, VesselRepair.Percent(15000));
        Assert.Equal(0, VesselRepair.Percent(0));
    }

    [Fact]
    public void Repairs_the_parts_below_the_threshold()
    {
        Assert.Equal([1, 3], VesselRepair.SlotsToRepair([100, 0, 50, 19], belowPercent: 20));
        Assert.Equal([1, 2, 3], VesselRepair.SlotsToRepair([100, 0, 50, 19], belowPercent: 100));
        Assert.Empty(VesselRepair.SlotsToRepair([100, 100, 100, 100], belowPercent: 100));
    }

    [Theory]
    [InlineData("12/345", 12, 345)]
    [InlineData(" 9 / 10 ", 9, 10)]
    public void Reads_fuel_and_distance_as_the_window_shows_them(string text, int used, int available) =>
        Assert.Equal((used, available), VesselRepair.Fraction(text));

    [Fact]
    public void Odd_fractions_are_unknown() => Assert.Null(VesselRepair.Fraction("—"));
}
