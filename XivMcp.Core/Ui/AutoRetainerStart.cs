using System;

namespace XivMcp.Ui;

/// <summary>What to do next while waiting for AutoRetainer to start at a summoning bell.</summary>
public enum AutoRetainerStartStep
{
    /// <summary>AutoRetainer is busy with the retainers.</summary>
    Started,

    /// <summary>A dialogue box (a venture report notice, an inn bell's greeting) is in front of the retainer list: advance it.</summary>
    AdvanceTalk,

    Wait,

    /// <summary>The retainer list is open but AutoRetainer stayed idle: it has nothing to do (or is set not to act at a bell).</summary>
    NothingToDo,

    /// <summary>Neither the list nor AutoRetainer came up in time (e.g. "You have not yet hired a retainer.").</summary>
    GiveUp,
}

/// <param name="Busy">AutoRetainer reports that it is working.</param>
/// <param name="TalkShown">A dialogue box ("Talk") is shown.</param>
/// <param name="ListOpen">The retainer list is shown.</param>
/// <param name="Elapsed">Since the bell was used.</param>
/// <param name="SinceAction">Since the last dialogue was advanced.</param>
/// <param name="SinceListOpened">Since the retainer list appeared; null while it hasn't.</param>
public sealed record AutoRetainerStartState(bool Busy, bool TalkShown, bool ListOpen, TimeSpan Elapsed, TimeSpan SinceAction, TimeSpan? SinceListOpened);

/// <summary>
/// AutoRetainer starts when the retainer list opens. A bell can show dialogue first (a retainer "has completed a venture!" when a
/// venture is back, or the inn bell's greeting), which is advanced like a click instead of being taken for "nothing to do".
/// </summary>
public static class AutoRetainerStart
{
    /// <summary>How long the list plus AutoRetainer's start may take, dialogues included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>How long AutoRetainer gets to start once the list is open.</summary>
    public static readonly TimeSpan ListGrace = TimeSpan.FromSeconds(8);

    /// <summary>Time between two dialogue clicks.</summary>
    public static readonly TimeSpan TalkPace = TimeSpan.FromMilliseconds(800);

    public static AutoRetainerStartStep Next(AutoRetainerStartState s)
    {
        if (s.Busy) return AutoRetainerStartStep.Started;
        if (s.ListOpen && s.SinceListOpened is { } open && open > ListGrace) return AutoRetainerStartStep.NothingToDo;
        if (s.Elapsed > Timeout) return AutoRetainerStartStep.GiveUp;
        if (s.TalkShown) return s.SinceAction >= TalkPace ? AutoRetainerStartStep.AdvanceTalk : AutoRetainerStartStep.Wait;
        return AutoRetainerStartStep.Wait;
    }
}
