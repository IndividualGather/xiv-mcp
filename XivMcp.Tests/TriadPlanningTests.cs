using System.Numerics;
using XivMcp.TripleTriad;

namespace XivMcp.Tests;

public class EorzeaClockTests
{
    [Fact]
    public void An_eorzea_hour_lasts_175_seconds()
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(0).UtcDateTime;
        Assert.Equal(0, EorzeaClock.MinuteOfDay(start));
        Assert.Equal(60, EorzeaClock.MinuteOfDay(start.AddSeconds(175)));
        Assert.Equal(0, EorzeaClock.MinuteOfDay(start.AddSeconds(175 * 24))); // a whole Eorzea day later
    }

    [Theory]
    [InlineData(0, 0, 13 * 60, true)]       // no window: always
    [InlineData(800, 1800, 12 * 60, true)]  // inside a day window
    [InlineData(800, 1800, 19 * 60, false)]
    [InlineData(2000, 400, 23 * 60, true)]  // a window across midnight
    [InlineData(2000, 400, 2 * 60, true)]
    [InlineData(2000, 400, 12 * 60, false)]
    public void Windows_are_hhmm_eorzea_time(int start, int end, int minute, bool open)
    {
        Assert.Equal(open, EorzeaClock.InWindow(start, end, minute));
    }
}

public class TriadAvailabilityTests
{
    [Fact]
    public void An_opponent_without_unlock_quests_is_open()
    {
        Assert.True(TriadAvailability.IsUnlocked(beaten: false, ownsAReward: false, quests: [], isComplete: _ => false));
    }

    [Fact]
    public void Any_listed_quest_unlocks_the_opponent()
    {
        Assert.False(TriadAvailability.IsUnlocked(false, false, [65800, 65801], _ => false));
        Assert.True(TriadAvailability.IsUnlocked(false, false, [65800, 65801], q => q == 65801));
    }

    [Fact]
    public void Having_played_them_shows_they_are_unlocked()
    {
        Assert.True(TriadAvailability.IsUnlocked(beaten: true, ownsAReward: false, quests: [65800], isComplete: _ => false));
        Assert.True(TriadAvailability.IsUnlocked(beaten: false, ownsAReward: true, quests: [65800], isComplete: _ => false));
    }
}

public class TriadFarmPlanTests
{
    private static TriadStop Stop(uint id, uint territory, string zone, float x, params int[] missing) =>
        new(id, $"Npc {id}", territory, zone, new Vector3(x, 0, 0), missing);

    [Fact]
    public void The_current_zone_comes_first_then_zones_by_name()
    {
        var plan = TriadFarmPlan.Order([Stop(1, 10, "Ul'dah", 0, 1), Stop(2, 20, "Gold Saucer", 0, 2), Stop(3, 30, "Limsa", 0, 3)], currentTerritory: 30, start: Vector3.Zero);
        Assert.Equal([3u, 2u, 1u], plan.Select(s => s.Id));
    }

    [Fact]
    public void Within_a_zone_the_nearest_opponent_is_next()
    {
        var plan = TriadFarmPlan.Order([Stop(1, 10, "A", 100, 1), Stop(2, 10, "A", 10, 2), Stop(3, 10, "A", 60, 3)], currentTerritory: 10, start: Vector3.Zero);
        Assert.Equal([2u, 3u, 1u], plan.Select(s => s.Id));
    }

    [Fact]
    public void A_card_two_opponents_give_is_counted_once_and_empty_stops_drop_out()
    {
        var plan = TriadFarmPlan.Order([Stop(1, 10, "A", 0, 5, 6), Stop(2, 10, "A", 10, 6)], currentTerritory: 10, start: Vector3.Zero);
        var only = Assert.Single(plan);
        Assert.Equal(1u, only.Id);
        Assert.Equal([5, 6], only.Missing);
    }

    [Fact]
    public void Max_stops_cuts_the_route()
    {
        var plan = TriadFarmPlan.Order([Stop(1, 10, "A", 0, 1), Stop(2, 10, "A", 10, 2), Stop(3, 10, "A", 20, 3)], 10, Vector3.Zero, maxStops: 2);
        Assert.Equal(2, plan.Count);
    }
}
