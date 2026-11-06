namespace XivMcp.Maps;

/// <summary>
/// Lifestream can't travel out of an inn room: its aethernet route walks to an aetheryte that isn't there, and its teleport is
/// refused. The game's own teleport works from an inn room, so the first hop is a plain teleport; a shard is then reached by
/// aethernet from its city's aetheryte.
/// </summary>
/// <param name="Teleport">Aetheryte to teleport to with the game's own teleport first, or null to use Lifestream right away.</param>
/// <param name="ThenAethernet">Whether the target shard still has to be reached by aethernet after that teleport.</param>
public readonly record struct InnExitPlan(uint? Teleport, bool ThenAethernet);

public static class InnExit
{
    /// <param name="intendedUse">TerritoryIntendedUse of where the player is.</param>
    /// <param name="target">The aetheryte or aethernet shard to travel to.</param>
    /// <param name="shard">Whether <paramref name="target"/> is an aethernet shard.</param>
    /// <param name="cityAetheryte">The main aetheryte of the shard's city, if known.</param>
    public static InnExitPlan Plan(uint intendedUse, uint target, bool shard, uint? cityAetheryte)
    {
        if (intendedUse != PlayerGuidance.InnUse) return new InnExitPlan(null, shard);
        if (!shard) return new InnExitPlan(target, false);
        return cityAetheryte is { } city and not 0 ? new InnExitPlan(city, true) : new InnExitPlan(null, true);
    }
}
