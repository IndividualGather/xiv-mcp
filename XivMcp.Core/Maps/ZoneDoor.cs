using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Maps;

/// <summary>
/// What a door into a zone asks of the player, from its Warp row's WarpCondition: quests, a fee and a class level. CompleteParam 1
/// needs every listed quest, 2 any one of them (read from the game data: the rows with 2 list the same main scenario step once per
/// starting city, as "The Gridanian Envoy", "The Ul'dahn Envoy" and "The Lominsan Envoy"). A door whose WarpLogic runs a script
/// may ask more than this.
/// </summary>
public sealed record DoorCondition(IReadOnlyList<uint> Quests, bool AnyQuest, uint Gil, ushort ClassLevel, bool Scripted)
{
    public static readonly DoorCondition None = new([], false, 0, 0, false);
}

/// <summary>Why a door won't open for the player now; null when it will (as far as the game data tells).</summary>
public sealed record DoorRefusal(IReadOnlyList<uint> MissingQuests, bool AnyQuest, uint Gil, ushort ClassLevel);

/// <summary>A door into a zone: the NPC or object to talk to, the zone it stands in, and its conditions.</summary>
public sealed record DoorChoice(uint ObjectId, uint Territory, bool HasOwnAetheryte, DoorCondition Condition);

public static class ZoneDoor
{
    /// <summary>Whether the door lets the player in: null when it does, else what is missing.</summary>
    public static DoorRefusal? Refusal(DoorCondition c, Func<uint, bool> questDone, uint gil, ushort classLevel)
    {
        var missing = c.Quests.Where(q => !questDone(q)).ToList();
        var questsOk = c.Quests.Count == 0 || (c.AnyQuest ? missing.Count < c.Quests.Count : missing.Count == 0);
        var gilOk = gil >= c.Gil;
        var levelOk = classLevel >= c.ClassLevel;
        return questsOk && gilOk && levelOk
            ? null
            : new DoorRefusal(questsOk ? [] : missing, c.AnyQuest, gilOk ? 0 : c.Gil, levelOk ? (ushort)0 : c.ClassLevel);
    }

    /// <summary>
    /// The door to use: one that opens for the player and stands in a zone the player can travel to (one with its own aetheryte or
    /// shard), the one in the player's zone first. Null when there is none; <see cref="Refusal"/> then tells why.
    /// </summary>
    public static DoorChoice? Pick(IEnumerable<DoorChoice> doors, uint playerTerritory, Func<DoorCondition, bool> opens) =>
        doors.Where(d => (d.HasOwnAetheryte || d.Territory == playerTerritory) && opens(d.Condition))
             .OrderBy(d => d.Territory == playerTerritory ? 0 : 1)
             .ThenBy(d => d.Condition.Scripted ? 1 : 0)
             .FirstOrDefault();
}
