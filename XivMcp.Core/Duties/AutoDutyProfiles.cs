using System;
using System.Collections.Generic;

namespace XivMcp.Duties;

/// <summary>
/// The AutoDuty profile run_duty farms with: a copy of the player's default profile, with XIV MCP's stop condition. It only lives
/// for the run; one left over from a crash is recognized by its name and removed before the next run.
/// </summary>
public static class AutoDutyProfiles
{
    public const string Temporary = "XivMcp_Farm";

    public static bool IsTemporary(string name) => name.StartsWith("XivMcp_", StringComparison.Ordinal);

    /// <summary>The profile to copy: the character's default, otherwise AutoDuty's global default, otherwise the active one.</summary>
    public static string Source(string? characterDefault, string? globalDefault, string active, IReadOnlyCollection<string> existing)
    {
        foreach (var candidate in new[] { characterDefault, globalDefault })
            if (candidate is not null && !IsTemporary(candidate) && Contains(existing, candidate)) return candidate;
        return IsTemporary(active) ? "Bare" : active;
    }

    private static bool Contains(IReadOnlyCollection<string> names, string name)
    {
        foreach (var n in names) if (n == name) return true;
        return false;
    }
}

/// <summary>A duty that drops an item, as AutoDuty knows it.</summary>
public sealed record DutyOption(string Name, uint TerritoryType, int Level, bool HasPath, IReadOnlyList<string> Modes);

/// <summary>Which duty to farm an item in.</summary>
public static class DutyChoice
{
    /// <summary>Modes that need no other players, best first.</summary>
    private static readonly string[] SoloModes = ["Support", "Trust", "Squadron"];

    /// <summary>
    /// The best duty to farm in: one AutoDuty has a path for, preferring one it can run with NPCs (Support, Trust, Squadron) over
    /// one that needs a party, then the lowest level. Null if AutoDuty can run none of them. Also returns the mode to use.
    /// </summary>
    public static (DutyOption Duty, string Mode)? Best(IEnumerable<DutyOption> options)
    {
        (DutyOption Duty, string Mode, int Rank)? best = null;
        foreach (var d in options)
        {
            if (!d.HasPath || d.Modes.Count == 0) continue;
            var solo = Array.FindIndex(SoloModes, m => Has(d.Modes, m));
            var mode = solo >= 0 ? SoloModes[solo] : d.Modes[0];
            var rank = solo >= 0 ? solo : SoloModes.Length;
            if (best is null || rank < best.Value.Rank || rank == best.Value.Rank && d.Level < best.Value.Duty.Level) best = (d, mode, rank);
        }
        return best is { } b ? (b.Duty, b.Mode) : null;
    }

    private static bool Has(IReadOnlyList<string> modes, string mode)
    {
        foreach (var m in modes) if (m == mode) return true;
        return false;
    }
}
