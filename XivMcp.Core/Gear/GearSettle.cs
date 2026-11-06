using System;

namespace XivMcp.Gear;

/// <summary>
/// A look at the character's gear. <see cref="JobMatches"/>: the job the caller waits for is the current one (true when it waits
/// for none). <see cref="Busy"/>: an inventory change is pending, a prompt is open or the character's state forbids changing gear.
/// <see cref="Equipped"/>: what is worn (any text that changes when the gear does, e.g. the item ids).
/// </summary>
public readonly record struct GearState(bool JobMatches, bool Busy, string Equipped);

/// <summary>
/// Waits out a gearset change: the game moves the set's pieces in one after another after the job has changed, and drops gear
/// moves sent meanwhile. Settled once the job is right, nothing is pending and the worn gear stayed the same for a quiet time.
/// </summary>
public sealed class GearSettle(TimeSpan quiet)
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromSeconds(1);

    /// <summary>How long switch_gearset waits for the job to change after equipping or answering a prompt.</summary>
    public static readonly TimeSpan JobChangeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the gear to settle before going on anyway.</summary>
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(15);

    private DateTime? since;
    private string? equipped;

    /// <summary>Records a look; true once the gear has been settled for the quiet time.</summary>
    public bool Observe(DateTime now, GearState state)
    {
        if (!state.JobMatches || state.Busy || state.Equipped != equipped)
        {
            equipped = state.Equipped;
            since = state.JobMatches && !state.Busy ? now : null;
            return false;
        }
        since ??= now;
        return now - since >= quiet;
    }
}

public enum EquipStep { Done, Retry, GiveUp }

/// <summary>Right after a job change the game may drop a gear move; try a refused item again before giving up.</summary>
public static class EquipRetry
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    /// <summary>What to do after attempt number <paramref name="attempt"/> (1-based) did or did not put the item on.</summary>
    public static EquipStep Next(int attempt, bool worn) =>
        worn ? EquipStep.Done : attempt < MaxAttempts ? EquipStep.Retry : EquipStep.GiveUp;

    public static string Refused(string item, int attempts) =>
        $"{item} was not put on after {attempts} tries (the game refused it; is the character busy, e.g. still changing job?).";
}
