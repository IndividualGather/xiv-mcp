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
    public void A_shard_without_a_known_city_aetheryte_falls_back_to_lifestream() =>
        Assert.Equal(new InnExitPlan(null, true), InnExit.Plan(PlayerGuidance.InnUse, target: 41, shard: true, cityAetheryte: null));
}
