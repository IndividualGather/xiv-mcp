using XivMcp.Maps;

namespace XivMcp.Tests;

public class InnExitTests
{
    [Fact]
    public void Outside_an_inn_lifestream_does_it_all() =>
        Assert.Equal(new InnExitPlan(null, true), InnExit.Plan(0, target: 41, shard: true, cityAetheryte: 8));

    [Fact]
    public void From_an_inn_room_an_aetheryte_is_reached_by_the_games_teleport() =>
        Assert.Equal(new InnExitPlan(8, false), InnExit.Plan(PlayerGuidance.InnUse, target: 8, shard: false, cityAetheryte: null));

    [Fact]
    public void From_an_inn_room_a_shard_is_reached_by_teleporting_to_its_city_then_aethernet() =>
        Assert.Equal(new InnExitPlan(8, true), InnExit.Plan(PlayerGuidance.InnUse, target: 41, shard: true, cityAetheryte: 8));

    [Fact]
    public void A_shard_name_two_cities_share_is_reached_through_its_city_first()
    {
        // "Airship Landing" is a shard in Limsa and in Gridania: "/li Airship Landing" from elsewhere can pick the wrong one.
        Assert.Equal(new InnExitPlan(8, true), InnExit.Plan(0, target: 41, shard: true, cityAetheryte: 8, ambiguousShardElsewhere: true));
    }

    [Fact]
    public void Outside_an_inn_an_aetheryte_needs_no_first_hop() =>
        Assert.Equal(new InnExitPlan(null, false), InnExit.Plan(0, target: 8, shard: false, cityAetheryte: null));

    [Fact]
    public void A_shard_without_a_known_city_aetheryte_falls_back_to_lifestream() =>
        Assert.Equal(new InnExitPlan(null, true), InnExit.Plan(PlayerGuidance.InnUse, target: 41, shard: true, cityAetheryte: null));
}
