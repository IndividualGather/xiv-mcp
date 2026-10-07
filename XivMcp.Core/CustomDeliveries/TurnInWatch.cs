using System;

namespace XivMcp.CustomDeliveries;

/// <summary>
/// Tells a delivery's turn-in from the rest of it (buying, crafting, gathering), and notices when turning in stalls: the stall clock
/// runs only while turning in, from when it started or the last delivery that went in, whichever is later.
/// </summary>
public sealed class TurnInWatch(TimeSpan limit)
{
    private DateTime? since;

    /// <summary>
    /// Questionable turns in what it gathered itself, once all of it is gathered. Before that, items in the bags only mean it is
    /// still gathering.
    /// </summary>
    public static bool QuestionableTurningIn(bool questionableRunning, int inBags, int needed) =>
        questionableRunning && needed > 0 && inBags >= needed;

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
