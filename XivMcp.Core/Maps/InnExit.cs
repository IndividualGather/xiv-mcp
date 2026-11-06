namespace XivMcp.Maps;

/// <summary>
/// The game's own teleport that comes before Lifestream's aethernet travel, if any. Lifestream can't travel out of an inn room
/// (its aethernet route walks to an aetheryte that isn't there, and its teleport is refused), and its "/li &lt;shard&gt;" command
/// resolves a shard by name alone, which several cities share ("Airship Landing" is in Limsa and in Gridania): from elsewhere,
/// it can pick the wrong city. So from an inn room, or for a shard whose name another city's shard has too, the first hop is a
/// plain teleport to the shard's city; the shard is then reached by aethernet from there.
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
    /// <param name="ambiguousShardElsewhere">The shard's name is shared with another city's shard, and the player is not in the shard's city.</param>
    public static InnExitPlan Plan(uint intendedUse, uint target, bool shard, uint? cityAetheryte, bool ambiguousShardElsewhere = false)
    {
        var inn = intendedUse == PlayerGuidance.InnUse;
        if (!shard) return inn ? new InnExitPlan(target, false) : new InnExitPlan(null, false);
        if (!inn && !ambiguousShardElsewhere) return new InnExitPlan(null, true);
        return cityAetheryte is { } city and not 0 ? new InnExitPlan(city, true) : new InnExitPlan(null, true);
    }
}
