using System;
using XivMcp.Farming;

namespace XivMcp.Tests;

public class MobFarmTests
{
    private static MobFarmAction Next(int have = 0, int wanted = 2, double hp = 100, bool dead = false, bool combat = false, bool target = false,
                                      double? nearest = null, double toSpawn = 5, double elapsed = 1, double sinceProgress = 1) =>
        MobFarm.Next(new MobFarmState(have, wanted, hp, dead, combat, target, nearest, toSpawn, TimeSpan.FromMinutes(elapsed),
            TimeSpan.FromMinutes(120), TimeSpan.FromMinutes(sinceProgress), TimeSpan.FromMinutes(10))).Action;

    [Fact] public void Enough_in_the_bags_is_done() => Assert.Equal(MobFarmAction.Done, Next(have: 2, combat: true));
    [Fact] public void Death_stops_the_farm() => Assert.Equal(MobFarmAction.Fail, Next(dead: true));
    [Fact] public void Low_hp_out_of_combat_rests_before_pulling_more() => Assert.Equal(MobFarmAction.Rest, Next(hp: 20, nearest: 10));
    [Fact] public void Mostly_full_hp_pulls_again() => Assert.Equal(MobFarmAction.Engage, Next(hp: 75, nearest: 10));
    [Fact] public void Low_hp_in_combat_keeps_fighting() => Assert.Equal(MobFarmAction.Fight, Next(hp: 20, combat: true));
    [Fact] public void A_fight_continues() => Assert.Equal(MobFarmAction.Fight, Next(target: true));
    [Fact] public void A_monster_in_sight_is_engaged() => Assert.Equal(MobFarmAction.Engage, Next(nearest: 25));
    [Fact] public void Far_from_the_spawn_with_none_in_sight_walks_back() => Assert.Equal(MobFarmAction.ReturnToSpawn, Next(toSpawn: 80));
    [Fact] public void At_the_spawn_with_none_in_sight_waits_for_a_respawn() => Assert.Equal(MobFarmAction.Wait, Next(toSpawn: 10));
    [Fact] public void Nothing_for_too_long_gives_up() => Assert.Equal(MobFarmAction.Fail, Next(sinceProgress: 11, nearest: 10));
    [Fact] public void The_timeout_stops_it() => Assert.Equal(MobFarmAction.Fail, Next(elapsed: 121));

    [Fact]
    public void The_best_combat_gearset_is_the_highest_complete_one()
    {
        var sets = new[]
        {
            new GearsetChoice(1, false, 700, 0),  // a crafter: never
            new GearsetChoice(2, true, 650, 0),
            new GearsetChoice(3, true, 690, 2),   // higher, but pieces are missing
            new GearsetChoice(4, true, 670, 0),
        };
        Assert.Equal(4, CombatGearset.Best(sets));
    }

    [Fact]
    public void Ranked_lists_every_combat_set_best_first_for_falling_back()
    {
        var sets = new[]
        {
            new GearsetChoice(1, true, 790, 0),  // looks complete until the game tries it
            new GearsetChoice(2, false, 800, 0),
            new GearsetChoice(3, true, 790, 0),
            new GearsetChoice(4, true, 700, 1),
            new GearsetChoice(5, true, 760, 0),
        };
        Assert.Equal([1, 3, 5, 4], CombatGearset.Ranked(sets));
    }

    [Fact] public void No_combat_gearset_is_null() => Assert.Null(CombatGearset.Best([new GearsetChoice(1, false, 700, 0)]));

    [Theory]
    [InlineData(1, 2.5f)]
    [InlineData(2, 2.5f)]
    [InlineData(3, 20f)]
    [InlineData(4, 20f)]
    public void Melee_stands_close_casters_at_range(byte role, float range) => Assert.Equal(range, MobFarm.Range(role));
}
