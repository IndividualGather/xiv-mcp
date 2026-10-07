using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Ui;

public enum RecoveryStep { Done, Blocked, AdvanceTalk, AnswerNo, LeaveMenu, CloseWindow, PressEscape, GiveUp }

/// <summary>
/// What recovery sees in one frame. <paramref name="Occupied"/>: the game holds the player in a conversation, menu or shop (they can't
/// move or act). <paramref name="Windows"/>: the windows that take input, innermost first, shown or hidden. <paramref name="Blocker"/>:
/// a state recovery must not interrupt (combat, a cutscene, loading). <paramref name="TriesOnTop"/>: how often the innermost window
/// was already acted on without going away.
/// </summary>
public sealed record RecoverySnapshot(bool Occupied, IReadOnlyList<string> Windows, string? Blocker, int EscapesSent, int TriesOnTop);

public sealed record RecoveryAction(RecoveryStep Step, string? Window = null, string? Reason = null);

/// <summary>
/// Gets the character out of whatever an automation left it in — a dialogue, a menu, a turn-in window, or an event that still holds
/// the player after its window vanished — the way a player would: click through dialogue, say no, leave menus, close windows, and as
/// a last resort press Escape. One decision per frame; the plugin acts on it and looks again.
/// </summary>
public static class RecoveryPlanner
{
    /// <summary>Attempts on one window (closing, leaving) before Escape is tried on it.</summary>
    public const int TriesPerWindow = 2;

    /// <summary>Lines of dialogue clicked through before Escape is tried on it.</summary>
    public const int MaxDialogueLines = 20;

    /// <summary>Escape presses before recovery gives up and asks the player.</summary>
    public const int MaxEscapes = 3;

    /// <summary>Windows that only show something (HUD, chat, name plates): never in the way, never closed.</summary>
    public static bool IsClosable(string window) =>
        !window.StartsWith('_') && !window.StartsWith("ChatLog", StringComparison.Ordinal) &&
        window is not ("NamePlate" or "ScreenLog" or "CursorAddon" or "ScenarioTree");

    public static RecoveryAction Next(RecoverySnapshot s)
    {
        if (s.Blocker is not null) return new(RecoveryStep.Blocked, Reason: $"The character is {s.Blocker}, which recovery doesn't interrupt.");

        var windows = s.Windows.Where(IsClosable).ToList();
        // Only the System menu (opened by an Escape with nothing left to close) is closed while nothing holds the player.
        if (!s.Occupied)
            return windows.FirstOrDefault() == "SystemMenu" ? new(RecoveryStep.CloseWindow, "SystemMenu") : new(RecoveryStep.Done);

        if (windows.Count == 0 || s.TriesOnTop >= (windows[0] == "Talk" ? MaxDialogueLines : TriesPerWindow))
            return s.EscapesSent < MaxEscapes
                ? new(RecoveryStep.PressEscape, windows.FirstOrDefault())
                : new(RecoveryStep.GiveUp, windows.FirstOrDefault(), windows.Count == 0
                    ? "The game still holds the character in a conversation, but no window is left to close, and Escape didn't end it."
                    : $"The window {windows[0]} doesn't close, not even with Escape.");

        var top = windows[0];
        return top switch
        {
            "Talk" => new(RecoveryStep.AdvanceTalk, top),
            "SelectYesno" => new(RecoveryStep.AnswerNo, top),
            "SelectString" or "SelectIconString" => new(RecoveryStep.LeaveMenu, top),
            _ => new(RecoveryStep.CloseWindow, top),
        };
    }
}
