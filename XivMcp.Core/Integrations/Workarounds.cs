using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Integrations;

/// <summary>
/// Something an integration does on behalf of the plugin it drives, where that plugin falls short (a step it hangs on, a prompt it
/// leaves open, a job it does not switch to): <paramref name="Does"/> says what XIV MCP does, <paramref name="Without"/> what the
/// plugin does on its own, one sentence each, for the player. <paramref name="CheckedAgainst"/> is the plugin version the gap was
/// last confirmed in: when the workaround stops firing or breaks, compare that version with the installed one. Update it whenever
/// the workaround is checked again or changed. Generated into the docs, so keep the format.
/// </summary>
public sealed record Workaround(string Id, string Does, string Without, string CheckedAgainst);

/// <summary>A workaround a run used, how often, and the plugin version it was checked against.</summary>
public sealed record WorkaroundUse(string Id, string Does, int Count, string CheckedAgainst);

/// <summary>What a job's result says about workarounds: the plugin, the version it ran with, and the workarounds used (maybe none).</summary>
public sealed record WorkaroundReport(string Plugin, string? Version, IReadOnlyList<WorkaroundUse> Used);

/// <summary>The workarounds one run of an integration's job used, and how often.</summary>
/// <param name="version">The plugin's installed version, for comparing with each workaround's CheckedAgainst.</param>
public sealed class WorkaroundLog(string pluginId, string? version)
{
    private readonly List<WorkaroundUse> used = [];

    /// <summary>Counts a workaround that just did its part.</summary>
    public void Used(Workaround workaround)
    {
        var i = used.FindIndex(u => u.Id == workaround.Id);
        if (i < 0) used.Add(new WorkaroundUse(workaround.Id, workaround.Does, 1, workaround.CheckedAgainst));
        else used[i] = used[i] with { Count = used[i].Count + 1 };
    }

    /// <summary>The plugin, its version and the workarounds used, in the order they were first used.</summary>
    public WorkaroundReport Report() => new(pluginId, version, used.ToList());
}

/// <summary>What XIV MCP does where Questionable falls short while it does quests (do_quests and the jobs that run it).</summary>
public static class QuestionableWorkarounds
{
    // On accepting, Questionable keeps the current job whenever the quest allows it, even below the quest's level, and otherwise
    // takes the job its settings name or the first allowed job with a gearset (Interact.cs, AcceptQuest).
    public static readonly Workaround PickJob = new("pick_job",
        "Before a quest is accepted, switches to a gearset of a job that may do it: your current job when it has the level, else the highest level one, and Miner or Botanist for gathering quests.",
        "Questionable accepts the quest on your current job when the quest allows it, whatever its level, else on the job its settings name.",
        "15.756.3.64");

    // Questionable switches back to the accepted job before an interaction only with its SameJobThroughoutQuest setting, which is
    // off by default (Interact.cs, Configuration.cs).
    public static readonly Workaround BackToAcceptedJob = new("back_to_accepted_job",
        "Before Questionable goes on with an accepted quest, switches back to the job it was accepted on, which a craft step can leave.",
        "Questionable does this only when its setting to switch to the job a quest was accepted with is on, which it is not by default.",
        "15.756.3.64");

    // Seen at a free company bell: AutoRetainer's retainer list was open and Questionable waited for a teleport that never came
    // (XIV MCP ed3d290). Not traced in Questionable's code.
    public static readonly Workaround CloseBlockingWindow = new("close_blocking_window",
        "Closes a window that holds your character, such as a shop or the retainer list at a bell, before starting Questionable.",
        "Questionable starts with the window open, and waits when it has to teleport.",
        "15.756.3.64");

    // Questionable's "Accept all quests" (QuestPriorityManager.MarkAcceptOnly) is not on its IPC, so XIV MCP reaches it through
    // reflection into QuestController.
    public static readonly Workaround AcceptTogether = new("accept_together",
        "Has Questionable accept several quests that are ready at once (often the day's allied society quests from one NPC) before doing any, as its Accept all quests does.",
        "Questionable accepts and does them one after another, going back to the NPC for each.",
        "15.756.3.64");

    // Questionable answers this prompt (Addon 102434) only while it runs and is on a quest (YesNoChoiceHandler.HandleTravelYesNo).
    // The Lance's Lesson and A Winter's Sale were seen stopping at it anyway (XIV MCP 037be68).
    public static readonly Workaround ConfirmHqTurnIn = new("confirm_hq_turn_in",
        "Answers Yes when a quest conversation asks whether to hand in a high-quality item.",
        "Questionable answers it while it runs, but turn-ins were seen stopping at the question.",
        "15.756.3.64");

    // The manual meld steps of the class quests are WaitForManualProgress with no skip condition (e.g. QuestPaths, CRP
    // 142_The Lance's Lesson.json); Skip is not on Questionable's IPC, so XIV MCP calls QuestController.Skip through reflection.
    public static readonly Workaround SkipMeldedStep = new("skip_melded_step",
        "Presses Questionable's Skip at a step that waits for you to meld materia on an item, when that item already has materia.",
        "Questionable waits at the step until you press Skip.",
        "15.756.3.64");

    // Craft.cs starts the quest's Artisan list before it counts the bags, and the list crafts every turn-in again, also past the step's
    // count. With the items owned, ShopController buys nothing, and Questionable was seen staying in the open shop. Its Skip only
    // stops it at such a step, so a step whose items it made itself is left to it (it finishes it once nothing is crafting).
    public static readonly Workaround SkipOwnedItemsStep = new("skip_owned_items_step",
        "Stops the Artisan list Questionable started for a craft step once the step's items are in your bags, and presses Questionable's Skip at a craft or purchase step whose items were already there when the step began.",
        "Questionable has Artisan craft the quest's items again, using up materials, and can stay in an open shop.",
        "15.756.3.64");

    // Once its priority list is done Questionable picks its next quest itself (QuestController, "New quest") and starts its tasks, a
    // teleport often first, within a second or two: faster than XIV MCP sees the quest done (seen 2026-11-25, Qitari dailies and
    // Chief Concerns, both followed by a teleport to Garlemald). StopAfterCurrentQuest is checked as its quest completes, before that
    // pick; it is not on the IPC, and every Stop clears it.
    public static readonly Workaround StopAfterRound = new("stop_after_round",
        "On the last quest XIV MCP gave Questionable, turns on its stop-after-this-quest flag, so it stops as that quest is done.",
        "Questionable goes on to a quest of its own choosing (from the journal or the main scenario), often with a teleport.",
        "15.756.3.64");

    public static IReadOnlyList<Workaround> All { get; } =
        [PickJob, BackToAcceptedJob, CloseBlockingWindow, AcceptTogether, ConfirmHqTurnIn, SkipMeldedStep, SkipOwnedItemsStep, StopAfterRound];
}

/// <summary>What XIV MCP does where Satisfier falls short in custom deliveries with player_ranks_up (deliver_custom_delivery and the jobs that run it).</summary>
public static class SatisfierWorkarounds
{
    // Satisfier turns in everything a request still takes in one go (AutoCommon.TurnIn), and its start IPC takes no count, so XIV MCP
    // lowers the client's MaxDeliveries on Satisfier's NPCInfo through reflection while it runs and puts it back after
    // (SatisfierBridge.LimitTurnIns). A count on Satisfier's start IPC would make this one unnecessary.
    public static readonly Workaround StopBeforeRankUp = new("stop_before_rank_up",
        "When you watch a rank-up yourself, lowers Satisfier's count of the client's deliveries while it runs, so it turns in only the deliveries before the rank-up.",
        "Satisfier turns in every delivery the request still takes in one go, the one that ranks the client up too.",
        "0.0.0.50");

    // Satisfier presses on every Talk window it sees while it turns in (AutoCommon.TurnIn, TurnInState.Talk), the client's story
    // after a rank-up included, so XIV MCP hands in that delivery itself (RankUpHandIn).
    public static readonly Workaround HandInRankUp = new("hand_in_rank_up",
        "When you watch a rank-up yourself, hands in the delivery that ranks the client up without Satisfier, and clicks nothing after Hand Over.",
        "Satisfier clicks through every line of dialogue while it turns in, the start of the client's story too.",
        "0.0.0.50");

    public static IReadOnlyList<Workaround> All { get; } = [StopBeforeRankUp, HandInRankUp];
}
