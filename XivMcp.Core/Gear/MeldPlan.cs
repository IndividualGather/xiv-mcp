using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Gear;

/// <summary>A materia to meld: its item, its grade (0-based: Materia I = 0) and whether it is a crafting/gathering materia (own overmeld table).</summary>
public readonly record struct MateriaPick(uint ItemId, string Name, int Grade, bool GatherCraft);

/// <summary>A piece of gear as melding sees it: its guaranteed slots, whether it can be overmelded and how many materia it holds.</summary>
public sealed record MeldItem(string Name, int GuaranteedSlots, bool AdvancedAllowed, int Filled);

/// <summary>The game's overmeld success chance in percent (0 = not allowed) for a materia grade in the n-th overmeld slot (0 = the first).</summary>
public delegate int OvermeldChance(int grade, int overmeldIndex, bool gatherCraft);

/// <summary>One meld: the slot (0-based over all five), the materia and its success chance (100 in a guaranteed slot).</summary>
public sealed record MeldStep(int Slot, MateriaPick Materia, int ChancePercent)
{
    public bool Overmeld => ChancePercent < 100;
}

public sealed class MeldPlanException(string message) : Exception(message);

/// <summary>
/// Which materia goes into which slot. Gear has up to five materia slots: its guaranteed ones always succeed; beyond them
/// (advanced melding, "overmelding") each try succeeds with the game's chance and a failed try uses the materia up.
/// Harder materia (higher grades) go first: into the guaranteed slots, then the early overmeld slots, where the chances are best.
/// </summary>
public static class MeldPlanner
{
    public const int MaxSlots = 5;

    public static List<MeldStep> Plan(MeldItem item, IReadOnlyList<MateriaPick> wanted, OvermeldChance chance, bool advancedUnlocked)
    {
        if (wanted.Count == 0) throw new MeldPlanException("No materia given.");
        if (item.GuaranteedSlots <= 0 && !item.AdvancedAllowed) throw new MeldPlanException($"{item.Name} can't take materia.");
        var total = item.Filled + wanted.Count;
        if (total > MaxSlots)
            throw new MeldPlanException($"{item.Name} holds at most {MaxSlots} materia; it has {item.Filled} and {wanted.Count} more were asked for.");
        var guaranteed = Math.Max(item.GuaranteedSlots, 0);
        if (total > guaranteed)
        {
            if (!item.AdvancedAllowed)
                throw new MeldPlanException($"{item.Name} has only {guaranteed} materia slot(s) and can't be overmelded; it has {item.Filled} and {wanted.Count} more were asked for.");
            if (!advancedUnlocked)
                throw new MeldPlanException($"Overmelding {item.Name} needs Advanced Materia Melding, which this character hasn't unlocked.");
        }

        var steps = new List<MeldStep>();
        var slot = item.Filled;
        foreach (var m in wanted.OrderByDescending(m => m.Grade))
        {
            var percent = 100;
            if (slot >= guaranteed)
            {
                var overmeld = slot - guaranteed;
                percent = chance(m.Grade, overmeld, m.GatherCraft);
                if (percent <= 0)
                    throw new MeldPlanException($"The game doesn't allow {m.Name} in overmeld slot {overmeld + 1} of {item.Name} (materia slot {slot + 1}).");
            }
            steps.Add(new MeldStep(slot, m, Math.Min(percent, 100)));
            slot++;
        }
        return steps;
    }

    /// <summary>How many materia the plan uses on average (a try at p% takes 100/p materia on average).</summary>
    public static double ExpectedMateria(IEnumerable<MeldStep> plan) => plan.Sum(s => 100.0 / s.ChancePercent);

    /// <summary>Another try at an overmeld after a failure: while materia is left and it failed fewer times than allowed.</summary>
    public static bool TryAgain(int failures, int maxFailures, int materiaLeft) => materiaLeft > 0 && failures < maxFailures;
}
