using System;

namespace XivMcp.CustomDeliveries;

/// <summary>
/// Tells a delivery's turn-in from the rest of it (buying, crafting, gathering), and notices when turning in stalls: the stall clock
/// runs only while turning in, from when it started or the last delivery that went in, whichever is later.
/// </summary>
public sealed class TurnInWatch(TimeSpan limit)
{
    private DateTime? since;
    private DateTime? blockedSince;

    /// <summary>How long a cutscene, a fight or a zone change may keep the character from getting out of a turn-in conversation.</summary>
    public static readonly TimeSpan BlockedLimit = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Satisfier's step while it turns in: "Turning in", then "Wait for CS" while the client's cutscene after the turn-in plays (a
    /// rank-up, the last delivery of the week).
    /// </summary>
    public static bool SatisfierTurningIn(string status) =>
        status.StartsWith("Turning in", StringComparison.OrdinalIgnoreCase) || status.StartsWith("Wait for CS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One look per poll while a conversation is in the way: whether something recovery doesn't interrupt (a cutscene, a fight, a zone
    /// change) has kept the character in it for longer than <see cref="BlockedLimit"/>. Cutscenes end on their own; only a long one is stuck.
    /// </summary>
    public bool BlockedTooLong(bool blocked, DateTime now)
    {
        if (!blocked)
        {
            blockedSince = null;
            return false;
        }
        blockedSince ??= now;
        return now - blockedSince.Value > BlockedLimit;
    }

    /// <summary>
    /// Questionable turns in what it gathered itself, once all of it is gathered. Before that, items in the bags only mean it is
    /// still gathering.
    /// </summary>
    public static bool QuestionableTurningIn(bool questionableRunning, int inBags, int needed) =>
        questionableRunning && needed > 0 && inBags >= needed;

    /// <summary>
    /// A gathering request whose items are all in the bags is turned in by Questionable directly. Satisfier would turn it in
    /// itself, straight through the game's agent, and that fails for some clients (Tiisol Ja); when items are still missing,
    /// Satisfier has Questionable gather them, and Questionable turns them in through the window, which works.
    /// </summary>
    public static bool TurnInWithQuestionable(bool isGather, int inBags, int toDeliver) => isGather && toDeliver > 0 && inBags >= toDeliver;

    /// <summary>One look per poll: whether turning in has gone on for longer than the limit without a delivery going in.</summary>
    public bool Stalled(bool turningIn, DateTime lastProgress, DateTime now)
    {
        if (!turningIn)
        {
            since = null;
            return false;
        }
        since ??= now;
        var from = lastProgress > since.Value ? lastProgress : since.Value;
        return now - from > limit;
    }
}
