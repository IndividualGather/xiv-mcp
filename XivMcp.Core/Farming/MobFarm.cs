using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Farming;

/// <summary>A saved gearset, as far as choosing one for fighting goes.</summary>
public readonly record struct GearsetChoice(int Number, bool CombatJob, int ItemLevel, int MissingItems);

public static class CombatGearset
{
    /// <summary>The combat gearset with the highest item level, complete sets first; null without any combat set.</summary>
    public static int? Best(IEnumerable<GearsetChoice> sets) =>
        sets.Where(s => s.CombatJob)
            .OrderBy(s => s.MissingItems > 0).ThenByDescending(s => s.ItemLevel)
            .Select(s => (int?)s.Number).FirstOrDefault();
}

public enum MobFarmAction
{
    /// <summary>The bags hold enough.</summary>
    Done,
    /// <summary>Give up (see the reason).</summary>
    Fail,
    /// <summary>Target the nearest living monster of the kind and go for it.</summary>
    Engage,
    /// <summary>A fight is on: keep the rotation going and stay in range.</summary>
    Fight,
    /// <summary>No monster in sight and far from where they live: go back there.</summary>
    ReturnToSpawn,
    /// <summary>At the spawn area, none in sight: wait for one to respawn.</summary>
    Wait,
}

public readonly record struct MobFarmDecision(MobFarmAction Action, string? Reason = null);

/// <param name="Have">How many of the item the bags hold.</param>
/// <param name="HpPercent">The character's HP, 0-100.</param>
/// <param name="NearestMob">Distance to the nearest living monster of the kind, or null when none is in sight.</param>
/// <param name="SinceProgress">Since the last kill or the last change of <paramref name="Have"/>.</param>
public sealed record MobFarmState(int Have, int Wanted, double HpPercent, bool Dead, bool InCombat, bool TargetAlive,
                                  double? NearestMob, double DistanceToSpawn, TimeSpan Elapsed, TimeSpan Timeout, TimeSpan SinceProgress, TimeSpan StallLimit);

/// <summary>
/// Farming monsters for a drop: fight the nearest one of the kind until the bags hold enough, wait at the spawn area for respawns,
/// and stop on death, low HP, the timeout or when nothing has happened for a long time (nothing spawns, nothing can be reached).
/// </summary>
public static class MobFarm
{
    /// <summary>Below this HP (percent) the farm stops instead of pulling more.</summary>
    public const double LowHp = 25;

    /// <summary>Farther than this from the spawn area with nothing in sight means walking back.</summary>
    public const double SpawnRadius = 30;

    public static MobFarmDecision Next(MobFarmState s)
    {
        if (s.Have >= s.Wanted) return new(MobFarmAction.Done);
        if (s.Dead) return new(MobFarmAction.Fail, "The character died.");
        if (s.HpPercent < LowHp && !s.InCombat) return new(MobFarmAction.Fail, $"HP is down to {s.HpPercent:0}%; stopped before pulling more.");
        if (s.Elapsed > s.Timeout) return new(MobFarmAction.Fail, "Timed out.");
        if (s.InCombat || s.TargetAlive) return new(MobFarmAction.Fight);
        if (s.SinceProgress > s.StallLimit)
            return new(MobFarmAction.Fail, $"No kill and no drop for {s.StallLimit.TotalMinutes:0} minutes (none spawn here, or they can't be reached).");
        if (s.NearestMob is not null) return new(MobFarmAction.Engage);
        return s.DistanceToSpawn > SpawnRadius ? new(MobFarmAction.ReturnToSpawn) : new(MobFarmAction.Wait);
    }

    /// <summary>How close to stand to the target: melee roles (tank, melee DPS) next to it, everyone else at casting range.</summary>
    public static float Range(byte role) => role is 1 or 2 ? 2.5f : 20f;
}
