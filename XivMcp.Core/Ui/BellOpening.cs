using System;

namespace XivMcp.Ui;

/// <summary>What to do next while a summoning bell opens.</summary>
public enum BellStep
{
    /// <summary>The retainer list is shown.</summary>
    Done,

    /// <summary>A dialogue box is in front of the list: advance it like a click.</summary>
    AdvanceTalk,

    /// <summary>The bell didn't answer at all: use it again (without the line-of-sight check).</summary>
    InteractAgain,

    Wait,

    /// <summary>The list didn't open in time.</summary>
    GiveUp,
}

/// <param name="ListOpen">The retainer list is shown (and its retainers are known).</param>
/// <param name="TalkShown">A dialogue box ("Talk") is shown.</param>
/// <param name="Reacted">The bell answered: the player is at the bell, or a dialogue was shown.</param>
/// <param name="Elapsed">Since the bell was first used.</param>
/// <param name="SinceInteract">Since the bell was last used.</param>
/// <param name="SinceAction">Since the last dialogue was advanced.</param>
/// <param name="Interactions">How often the bell was used so far.</param>
public sealed record BellState(bool ListOpen, bool TalkShown, bool Reacted, TimeSpan Elapsed, TimeSpan SinceInteract, TimeSpan SinceAction, int Interactions);

/// <summary>
/// Opening the retainer list at a summoning bell. Some bells (the Mizzenmast Inn's) show one or two dialogue boxes before the list;
/// those are advanced at a player's pace. A bell that doesn't answer at all is used once more.
/// </summary>
public static class BellOpening
{
    /// <summary>How long the list may take, dialogues included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>How long to wait for any answer before using the bell again.</summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(3);

    /// <summary>Time between two dialogue clicks.</summary>
    public static readonly TimeSpan TalkPace = TimeSpan.FromMilliseconds(800);

    public static BellStep Next(BellState s)
    {
        if (s.ListOpen) return BellStep.Done;
        if (s.Elapsed > Timeout) return BellStep.GiveUp;
        if (s.TalkShown) return s.SinceAction >= TalkPace ? BellStep.AdvanceTalk : BellStep.Wait;
        if (!s.Reacted && s.Interactions < 2 && s.SinceInteract >= AnswerWait) return BellStep.InteractAgain;
        return BellStep.Wait;
    }
}
