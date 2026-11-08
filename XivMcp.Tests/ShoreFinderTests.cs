using System;
using System.Linq;
using XivMcp.Fishing;

namespace XivMcp.Tests;

public class ShoreFinderTests
{
    // An island of radius 10 around the origin, in water.
    private static bool Island(double x, double z) => x * x + z * z <= 100;

    [Fact]
    public void From_land_the_shore_is_where_the_ground_ends()
    {
        var points = ShoreFinder.Candidates(0, 0, 30, Island, 0, 0);
        Assert.Equal(16, points.Count);
        Assert.All(points, p =>
        {
            var r = Math.Sqrt(p.X * p.X + p.Z * p.Z);
            Assert.InRange(r, 8.4, 10);                                  // the last walkable step
            Assert.True(Math.Sqrt(p.InlandX * p.InlandX + p.InlandZ * p.InlandZ) < r); // inland is towards the centre
        });
    }

    [Fact]
    public void Nearest_shore_comes_first_and_faces_the_water()
    {
        var p = ShoreFinder.Candidates(0, 0, 30, Island, 0, 20)[0];
        Assert.True(p.Z > 8);                       // the north shore, nearest to the character
        Assert.InRange(p.Angle, -0.01, 0.01);       // facing north, out to sea
    }

    [Fact]
    public void From_the_water_the_shore_is_the_first_ground_and_the_water_is_behind()
    {
        // Ground only north of z = 12 (a coastline), the spot's centre out on the water.
        var points = ShoreFinder.Candidates(0, 0, 30, (x, z) => z >= 12, 0, 40);
        var north = points.First();
        Assert.InRange(north.Z, 12, 13.5);
        Assert.InRange(north.Angle, Math.PI - 0.01, Math.PI + 0.01); // facing south, back to the water
        Assert.True(north.InlandZ > north.Z);
    }

    [Fact]
    public void No_shore_within_reach_finds_nothing()
    {
        Assert.Empty(ShoreFinder.Candidates(0, 0, 30, (_, _) => true, 0, 0));
        Assert.Empty(ShoreFinder.Candidates(0, 0, 30, (_, _) => false, 0, 0));
    }

    [Fact]
    public void Walkable_everywhere_finds_no_shore()
    {
        // A shallow river bed is navmesh too: the ground never ends, so there is no edge to find.
        Assert.Empty(ShoreFinder.Candidates(0, 0, 30, (_, _) => true, 0, 0));
    }

    [Fact]
    public void The_sweep_walks_out_in_every_direction_to_the_search_radius()
    {
        var targets = ShoreFinder.Sweep(100, 200, 40, directions: 8);
        Assert.Equal(8, targets.Count);
        Assert.All(targets, t => Assert.Equal(40, Math.Sqrt((t.X - 100) * (t.X - 100) + (t.Z - 200) * (t.Z - 200)), 3));
        Assert.Equal(8, targets.Select(t => (Math.Round(t.X), Math.Round(t.Z))).Distinct().Count());
    }

    [Fact]
    public void The_approach_starts_inland_so_the_character_arrives_facing_the_water()
    {
        // Edge at (10, 0), the cloud sea (spot centre) at the origin: start 3 yalms further out from the centre.
        var (x, z) = ShoreFinder.ApproachFrom(10, 0, 0, 0, inland: 3);
        Assert.Equal(13, x, 6);
        Assert.Equal(0, z, 6);
    }

    [Fact]
    public void The_approach_at_the_centre_itself_stays_put() => Assert.Equal((5.0, 5.0), ShoreFinder.ApproachFrom(5, 5, 5, 5));
}