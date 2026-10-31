using System.Text;

namespace XivMcp.Gear;

/// <summary>What may be done to a saved gearset, and why not, before the game is asked to.</summary>
public static class GearsetEdit
{
    public enum Action { Save, Delete, Rename }

    /// <summary>A gearset as the player sees it: its number (1-based), name and job, and whether it is worn.</summary>
    public sealed record Target(int Number, string Name, uint JobId, string Job, bool Current);

    /// <summary>The game keeps a set's name in 48 bytes, one of them the terminating zero.</summary>
    public const int MaxNameBytes = 47;

    /// <summary>Null when the action can go ahead; otherwise what stands in the way and how to get past it.</summary>
    public static string? Problem(Action action, Target set, uint currentJob, string currentJobAbbreviation, string? newName) => action switch
    {
        // The game saves what is worn, job included: saving another job's gear would turn the set into that job's.
        Action.Save when currentJob != set.JobId =>
            $"Gearset {set.Number} ({set.Name}) is a {set.Job} set, but you are {currentJobAbbreviation}: wear a {set.Job} set first (with 'from', or switch_gearset).",
        Action.Delete when set.Current => $"Gearset {set.Number} ({set.Name}) is the one you are wearing: switch to another set before deleting it.",
        Action.Rename when string.IsNullOrWhiteSpace(newName) => "Give the new name in 'name'.",
        Action.Rename when Encoding.UTF8.GetByteCount(newName!) > MaxNameBytes => $"Gearset names can be at most {MaxNameBytes} bytes long.",
        _ => null,
    };
}
