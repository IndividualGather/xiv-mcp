using XivMcp.Maps;

namespace XivMcp.Tests;

public class ZoneDoorTests
{
    private static bool Done(uint q) => q == 1;

    [Fact]
    public void A_door_without_conditions_opens() =>
        Assert.Null(ZoneDoor.Refusal(DoorCondition.None, Done, gil: 0, classLevel: 1));

    [Fact]
    public void Every_listed_quest_is_needed_unless_any_one_will_do()
    {
        var all = new DoorCondition([1, 2], AnyQuest: false, 0, 0, false);
        Assert.Equal([2u], ZoneDoor.Refusal(all, Done, 0, 1)!.MissingQuests);
        Assert.Null(ZoneDoor.Refusal(all with { AnyQuest = true }, Done, 0, 1));
        Assert.NotNull(ZoneDoor.Refusal(new DoorCondition([2, 3], AnyQuest: true, 0, 0, false), Done, 0, 1));
    }

    [Fact]
    public void A_fee_and_a_class_level_are_checked()
    {
        var c = new DoorCondition([], false, Gil: 80, ClassLevel: 10, false);
        var refusal = ZoneDoor.Refusal(c, Done, gil: 50, classLevel: 9)!;
        Assert.Equal(80u, refusal.Gil);
        Assert.Equal((ushort)10, refusal.ClassLevel);
        Assert.Null(ZoneDoor.Refusal(c, Done, gil: 80, classLevel: 10));
    }

    [Fact]
    public void The_door_in_the_players_zone_comes_first_and_a_door_only_reached_through_another_door_is_left_out()
    {
        var far = new DoorChoice(10, Territory: 419, HasOwnAetheryte: true, DoorCondition.None);
        var here = new DoorChoice(11, Territory: 418, HasOwnAetheryte: true, DoorCondition.None);
        var nested = new DoorChoice(12, Territory: 433, HasOwnAetheryte: false, DoorCondition.None);
        Assert.Equal(here, ZoneDoor.Pick([far, here, nested], playerTerritory: 418, _ => true));
        Assert.Equal(far, ZoneDoor.Pick([nested, far], playerTerritory: 1, _ => true));
        Assert.Null(ZoneDoor.Pick([nested], playerTerritory: 1, _ => true));
        Assert.Equal(nested, ZoneDoor.Pick([nested], playerTerritory: 433, _ => true));
    }

    [Fact]
    public void A_closed_door_is_never_picked()
    {
        var closed = new DoorChoice(10, 419, true, new DoorCondition([2], false, 0, 0, false));
        Assert.Null(ZoneDoor.Pick([closed], 1, c => ZoneDoor.Refusal(c, Done, 0, 1) is null));
    }
}
