using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMcp.Ui;

namespace XivMcp.Util;

/// <summary>
/// Gets the character out of a conversation, menu or turn-in window an automation left behind, or out of an event that still holds
/// the player after its window vanished (the game then refuses every interaction). Decisions come from <see cref="RecoveryPlanner"/>;
/// this reads the game and carries them out. Used by recover_game_state and after every job step that fails or is stopped.
/// </summary>
internal static class Recovery
{
    public sealed record Result(bool Recovered, List<string> Actions, string? Problem);

    /// <summary>Windows of conversations, menus, turn-ins and shops, innermost first.</summary>
    private static readonly string[] EventWindows =
    [
        "Talk", "SelectYesno", "InputNumeric", "Request", "SatisfactionSupplyResult", "SatisfactionSupply", "Bank",
        "ShopExchangeCurrency", "ShopExchangeItem", "Shop", "InclusionShop", "GrandCompanySupplyList", "CollectablesShop",
        "SelectString", "SelectIconString", "RetainerList", "SystemMenu",
    ];

    private static readonly ConditionFlag[] OccupiedFlags =
    [
        ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent, ConditionFlag.Occupied, ConditionFlag.Occupied30,
        ConditionFlag.Occupied33, ConditionFlag.Occupied38, ConditionFlag.Occupied39, ConditionFlag.OccupiedSummoningBell,
    ];

    private static readonly (ConditionFlag Flag, string What)[] Blockers =
    [
        (ConditionFlag.InCombat, "in combat"),
        (ConditionFlag.BetweenAreas, "loading a zone"), (ConditionFlag.BetweenAreas51, "loading a zone"),
        (ConditionFlag.WatchingCutscene, "watching a cutscene"), (ConditionFlag.WatchingCutscene78, "watching a cutscene"),
        (ConditionFlag.OccupiedInCutSceneEvent, "watching a cutscene"),
        (ConditionFlag.ExecutingCraftingAction, "crafting"), (ConditionFlag.ExecutingGatheringAction, "gathering"),
        (ConditionFlag.Casting, "casting"),
    ];

    /// <summary>Whether the game holds the character in a conversation, menu or shop right now. Framework thread.</summary>
    public static bool Occupied => OccupiedFlags.Any(f => Svc.Condition[f]);

    /// <summary>The game holds the character in a conversation, but no window of it is left (shown or hidden). Framework thread.</summary>
    public static bool OrphanedEvent => Occupied && !Windows().Any(RecoveryPlanner.IsClosable);

    /// <summary>
    /// Runs recovery until the character is free, recovery is blocked (combat, a cutscene, loading), or it gives up. Never throws for
    /// the game's state; the result says what was done and what is left.
    /// </summary>
    public static async Task<Result> Run(CancellationToken ct, TimeSpan? timeout = null)
    {
        var actions = new List<string>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        var escapes = 0;
        string? lastWindow = null;
        var tries = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (occupied, windows, blocker) = await Game.Run(Read).ConfigureAwait(false);
            var top = windows.FirstOrDefault(RecoveryPlanner.IsClosable);
            var triesOnTop = top is not null && top == lastWindow ? tries : 0;
            var action = RecoveryPlanner.Next(new RecoverySnapshot(occupied, windows, blocker, escapes, triesOnTop));
            switch (action.Step)
            {
                case RecoveryStep.Done:
                    return new Result(true, actions, null);
                case RecoveryStep.Blocked:
                case RecoveryStep.GiveUp:
                    return new Result(false, actions, action.Reason);
            }
            if (DateTime.UtcNow > deadline)
                return new Result(false, actions, $"Still not free after {(timeout ?? TimeSpan.FromSeconds(20)).TotalSeconds:0} seconds (last: {actions.LastOrDefault() ?? "nothing done"}).");

            actions.Add(await Game.Run(() => Act(action)).ConfigureAwait(false));
            if (action.Step == RecoveryStep.PressEscape)
            {
                escapes++;
                await PressEscape(ct).ConfigureAwait(false);
            }
            if (action.Window is not null && action.Window == lastWindow) tries++;
            else { lastWindow = action.Window; tries = 1; }
            await Task.Delay(800, ct).ConfigureAwait(false);
        }
    }

    private static (bool Occupied, List<string> Windows, string? Blocker) Read() =>
        (Occupied, Windows(), Blockers.FirstOrDefault(b => Svc.Condition[b.Flag]).What);

    /// <summary>
    /// The shown windows that take input: the ones the game has focused (innermost first), then shown event windows. Hidden windows
    /// are never acted on: the game keeps used windows loaded and hidden (Talk, SelectString, ...), and acting on a hidden custom
    /// delivery window crashed the game (ffxiv_dx11.exe+5EF177, twice). A conversation held without a shown window gets Escape.
    /// </summary>
    private static unsafe List<string> Windows()
    {
        var names = new List<string>();
        var manager = AtkStage.Instance()->RaptureAtkUnitManager;
        if (manager != null)
        {
            ref var focused = ref manager->AtkUnitManager.FocusedUnitsList;
            for (var i = focused.Count - 1; i >= 0; i--)
            {
                var addon = focused.Entries[i].Value;
                if (addon != null && addon->IsVisible && addon->NameString is { Length: > 0 } name && !names.Contains(name)) names.Add(name);
            }
        }
        foreach (var name in EventWindows)
            if (!names.Contains(name) && RetainerUi.Ready(name)) names.Add(name);
        return names;
    }

    /// <summary>Carries out one decision (Escape is pressed by the caller, off the framework thread). Framework thread.</summary>
    private static unsafe string Act(RecoveryAction action)
    {
        var addon = action.Window is { } w ? (AtkUnitBase*)RetainerUi.Ptr(w).Address : null;
        switch (action.Step)
        {
            case RecoveryStep.AdvanceTalk:
                RetainerUi.ClickTalk();
                return "Clicked through the dialogue.";
            case RecoveryStep.AnswerNo when addon != null:
                // The question says what was waiting (e.g. that a turn-in would go over a currency's cap): keep it for the report.
                var question = GameWindows.AllTexts(addon).OrderByDescending(t => t.Length).FirstOrDefault();
                RetainerUi.Fire(addon, true, 1);
                return question is null ? "Answered No." : $"Answered No to \"{question.ReplaceLineEndings(" ")}\".";
            case RecoveryStep.LeaveMenu or RecoveryStep.CloseWindow when addon != null:
                var hidden = !addon->IsVisible;
                addon->Close(true);
                return action.Step == RecoveryStep.LeaveMenu ? $"Left the menu ({action.Window})." : $"Closed {action.Window}{(hidden ? " (it was loaded but hidden)" : "")}.";
            case RecoveryStep.PressEscape:
                // Escape clears the target before it closes anything: clear it first, so the press goes to the conversation.
                TargetSystem.Instance()->Target = null;
                return action.Window is null ? "Pressed Escape: the game still held the character, with no window left." : $"Pressed Escape: {action.Window} didn't close.";
            default:
                return $"{action.Window} was already gone.";
        }
    }

    // ------------------------------------------------------------------ Escape, as a key press to the game window

    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101;
    private const nint VkEscape = 0x1B;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private static async Task PressEscape(CancellationToken ct)
    {
        var window = await Game.Run(GameWindow).ConfigureAwait(false);
        if (window == 0) return;
        PostMessage(window, WmKeyDown, VkEscape, 0x00010001);
        await Task.Delay(60, ct).ConfigureAwait(false);
        PostMessage(window, WmKeyUp, VkEscape, unchecked((nint)0xC0010001));
    }

    private static unsafe nint GameWindow()
    {
        var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        return framework != null && framework->GameWindow != null && framework->GameWindow->WindowHandle != 0
            ? framework->GameWindow->WindowHandle
            : Process.GetCurrentProcess().MainWindowHandle;
    }
}
