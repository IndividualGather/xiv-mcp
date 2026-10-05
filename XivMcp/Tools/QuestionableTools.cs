using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Quests;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Questing with Questionable (integration "Questionable"): a quest with the quests it needs first, the day's allied society
/// quests with the rank-up quests once the reputation bar is full, and the main scenario. XIV MCP works out what to do from the
/// game's Quest sheet, hands it to Questionable as its priority list (the player's own list is put back afterwards) and watches
/// until it is done. Stopping waits for a fight to end first. Questionable's IPC: IsRunning, GetCurrentQuestId, StartQuest,
/// IsQuestComplete, IsReadyToAcceptQuest, IsQuestAccepted, IsQuestLockedReason, Clear/Add/Import/ExportQuestPriority and Stop.
/// </summary>
internal static class QuestionableTools
{
    internal const string PluginId = "Questionable";

    /// <summary>The journal genre icon of main scenario quests.</summary>
    private const uint MainScenarioIcon = 61412;

    public static IEnumerable<McpTool> Create(Func<JobManager> jobs, Func<string?> client)
    {
        yield return new McpTool
        {
            Name = "get_questing_status",
            Description = "What Questionable is doing: whether it runs, its current quest, its priority list, the next main scenario quest, " +
                          "the allied society (beast tribe) allowances left today, and the reputation with each allied society.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(Status),
        };

        yield return new McpTool
        {
            Name = "complete_quest",
            Description = "Starts a background job that completes a quest with Questionable, together with the quests it needs first: " +
                          "XIV MCP works out the chain from the game data, leaves out what is complete, checks that Questionable has a path " +
                          "for each quest, and lets Questionable do them in order (accepting, walking, talking, fighting with its combat " +
                          "module). Stops when the quest is complete. Follow it with get_job; stop_questing or cancel_job stops it (after a " +
                          "fight). Questionable's own priority list is put back afterwards. Quests Questionable has no path for are named before " +
                          "starting. When Questionable reaches a step it leaves to the player (a duty it cannot run, a solo duty, something " +
                          "by hand), the job fails with what Questionable said; when it stops making progress, it is started again.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "quest": { "type": "string", "description": "The quest's name or id (the Quest sheet row, or the quest number Questionable shows)." },
                    "timeout_minutes": { "type": "integer", "minimum": 5, "maximum": 1440, "description": "Give up after this long (default 240)." }
                  },
                  "required": ["quest"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var query = args.String("quest") ?? throw new ToolException("'quest' is required.");
                var (target, plan, alternatives) = await Game.RunLoggedIn(() => { RequireLoaded(); return PlanQuest(query); }).ConfigureAwait(false);
                if (plan.Quests.Count == 0) return new { done = true, message = $"{target.Name} is already complete." };
                Refuse(plan.Problems);
                var unknown = await Game.Run(() => Unsupported(plan.Quests)).ConfigureAwait(false);
                if (unknown.Count > 0)
                    throw new ToolException($"Questionable has no path for {Join(unknown.Select(q => q.Name))}. Do {(unknown.Count == 1 ? "it" : "them")} yourself first, then try again.");

                var job = jobs().Start($"Quest: {target.Name}",
                    [new JobManager.Step
                    {
                        Id = "quests", Tool = "do_quests",
                        Args = new JsonObject { ["quest"] = target.Id.ToString(), ["timeout_minutes"] = args.Int("timeout_minutes", 240, 5, 1440) },
                        Note = plan.Quests.Count == 1 ? $"Do {target.Name}" : $"Do {target.Name} and the {plan.Quests.Count - 1} quests before it",
                    }], client());
                return new
                {
                    started = JobManager.Describe(job),
                    quests = plan.Quests.Select(Describe),
                    duties = await Game.Run(() => Duties(plan.Quests)).ConfigureAwait(false),
                    levelWarning = await Game.Run(() => LevelWarning(plan.Quests) is { Length: > 0 } w ? w.Trim() : null).ConfigureAwait(false),
                    otherQuestsNamedSo = alternatives.Count > 0 ? alternatives.Select(Describe) : null,
                };
            },
        };

        yield return new McpTool
        {
            Name = "do_tribe_dailies",
            Description = "Starts a background job that does today's quests for one allied society (beast tribe) with Questionable, to farm " +
                          "its reputation: the quests already accepted, the rank-up and story quests that open once the reputation bar is " +
                          "full (shown in blue in the game), and the day's daily quests the allowances left allow. After each quest it looks " +
                          "again, so a rank-up reached on the way is done too. Follow it with get_job; stop_questing or cancel_job stops it " +
                          "(after a fight). Questionable's own priority list is put back afterwards.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "tribe": { "type": "string", "description": "The allied society's name, such as Amalj'aa, Pelupelu or Yok Huy (see get_questing_status)." },
                    "timeout_minutes": { "type": "integer", "minimum": 5, "maximum": 1440, "description": "Give up after this long (default 180)." }
                  },
                  "required": ["tribe"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var query = args.String("tribe") ?? throw new ToolException("'tribe' is required.");
                var (tribe, plan, rank) = await Game.RunLoggedIn(() =>
                {
                    RequireLoaded();
                    var t = FindTribe(query);
                    return (t, PlanTribe(t.Id), TribeRank(t.Id));
                }).ConfigureAwait(false);
                if (rank.Rank == 0)
                {
                    var unlock = await Game.Run(() => TribeUnlockQuest(tribe.Id)).ConfigureAwait(false);
                    throw new ToolException($"The {tribe.Name} are not unlocked yet." + (unlock is { } u ? $" Their quests start with {u.Name}: complete_quest can do it, with the quests before it." : ""));
                }
                if (plan.Count == 0)
                {
                    var allowances = await Game.Run(Allowances).ConfigureAwait(false);
                    return new
                    {
                        done = true,
                        message = allowances == 0 ? "No allied society allowances are left today. They come back at the daily reset (15:00 UTC)."
                                                  : $"There is nothing more to do for the {tribe.Name} today.",
                        reputation = rank,
                    };
                }
                var unknown = await Game.Run(() => { RefuseWrongJob(plan); return Unsupported(plan); }).ConfigureAwait(false);
                var job = jobs().Start($"Allied society quests: {tribe.Name}",
                    [new JobManager.Step
                    {
                        Id = "quests", Tool = "do_quests",
                        Args = new JsonObject { ["tribe"] = tribe.Id.ToString(), ["timeout_minutes"] = args.Int("timeout_minutes", 180, 5, 1440) },
                        Note = $"Do today's {tribe.Name} quests",
                    }], client());
                return new
                {
                    started = JobManager.Describe(job),
                    quests = plan.Select(Describe),
                    noPath = unknown.Count > 0 ? unknown.Select(q => q.Name) : null,
                    reputation = rank,
                };
            },
        };

        yield return new McpTool
        {
            Name = "do_quests",
            Description = "Does quests with Questionable until they are done: with 'quest', that quest and the quests it needs first; with " +
                          "'tribe', the day's quests of an allied society (rank-up quests included). Looks again after each quest. Gives " +
                          "Questionable the quests as its priority list (its own list is put back afterwards) and stops it when done, after a " +
                          "fight. Starts Questionable again when the character stands still for 2 minutes (at most 3 times). Fails when Questionable stops on its own, waits for the player (a duty, a solo duty, a step by hand) or turns to another quest. Meant as a job step; complete_quest and " +
                          "do_tribe_dailies start it.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "quest": { "type": "string", "description": "The quest's name or id." },
                    "tribe": { "type": "string", "description": "The allied society's name or id." },
                    "timeout_minutes": { "type": "integer", "minimum": 5, "maximum": 1440, "description": "Give up after this long (default 240)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var quest = args.String("quest");
                var tribeQuery = args.String("tribe");
                if (quest is null == tribeQuery is null) throw new ToolException("Give either 'quest' or 'tribe'.");
                var timeout = TimeSpan.FromMinutes(args.Int("timeout_minutes", 240, 5, 1440));
                if (quest is not null)
                {
                    var target = await Game.RunLoggedIn(() => { RequireLoaded(); return PlanQuest(quest).Target; }).ConfigureAwait(false);
                    return await Run(() =>
                    {
                        var plan = QuestChain.Plan(target.Id, Find, IsComplete, KnownToQuestionable);
                        Refuse(plan.Problems);
                        return plan.Quests;
                    }, timeout, ct).ConfigureAwait(false);
                }
                var tribe = await Game.RunLoggedIn(() => { RequireLoaded(); return FindTribe(tribeQuery!); }).ConfigureAwait(false);
                var result = await Run(() => PlanTribe(tribe.Id), timeout, ct).ConfigureAwait(false);
                return new { result.done, reputation = await Game.Run(() => TribeRank(tribe.Id)).ConfigureAwait(false) };
            },
        };

        yield return new McpTool
        {
            Name = "start_main_scenario",
            Description = "Starts Questionable on the main scenario: it does the next main scenario quest and carries on with the ones after " +
                          "it (and the quests on its priority list first) until it is stopped with stop_questing, or until it cannot go on " +
                          "(a duty, a level it does not have). To stop at a certain main scenario quest, use complete_quest with that quest.",
            ReadOnly = false,
            Handler = async (_, _) =>
            {
                var (running, next) = await Game.RunLoggedIn(() => { RequireLoaded(); return (IsRunning(), NextMainScenarioQuest()); }).ConfigureAwait(false);
                if (running) return "Questionable is already running. stop_questing stops it.";
                if (next is { } quest)
                {
                    var started = await Game.Run(() => Ipc<string, bool>("StartQuest", quest.QuestionableId)).ConfigureAwait(false);
                    if (!started) throw new ToolException($"Questionable has no path for the next main scenario quest, {quest.Name}.");
                    return new { started = true, quest = Describe(quest) };
                }
                // The game names no next quest (the main scenario is waiting on something): let Questionable look for itself.
                await Game.Run(() => { GameCommands.Execute("/qst start"); return true; }).ConfigureAwait(false);
                return new { started = true, quest = (object?)null, note = "The game names no next main scenario quest right now; Questionable was started and looks for one itself." };
            },
        };

        yield return new McpTool
        {
            Name = "stop_questing",
            Description = "Stops Questionable, but never mid-fight: it keeps fighting until the character is out of combat, then stops. A " +
                          "running complete_quest or do_tribe_dailies job is paused (resume_job carries on).",
            ReadOnly = false,
            Handler = async (_, ct) =>
            {
                var paused = new List<string>();
                foreach (var job in jobs().All().Where(j => j.State == JobManager.JobState.Running && j.Current?.Tool == "do_quests"))
                {
                    jobs().Pause(job.Id, "Paused by stop_questing; resume_job to carry on.");
                    paused.Add(job.Name);
                }
                var wasRunning = await Game.Run(() => Loaded && IsRunning()).ConfigureAwait(false);
                var fought = await StopAfterFight(ct).ConfigureAwait(false);
                if (!wasRunning && paused.Count == 0) return "Questionable is not running.";
                return new { stopped = true, afterFight = fought, pausedJobs = paused.Count > 0 ? paused : null };
            },
        };
    }

    // ------------------------------------------------------------------ Running

    private sealed record RunResult(bool done, List<string> completed);

    /// <summary>
    /// Gives Questionable the planned quests and watches it, planning again whenever one of them is done, until nothing is left.
    /// Its priority list is put back and it is stopped (after a fight) at the end, however the run ends.
    /// </summary>
    private static async Task<RunResult> Run(Func<IReadOnlyList<QuestInfo>> plan, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        var backup = await Game.Run(() => Ipc<string>("ExportQuestPriority")).ConfigureAwait(false);
        var completed = new List<string>();
        using var chat = new QuestionableChat();
        try
        {
            while (true)
            {
                var quests = await Game.Run(plan).ConfigureAwait(false);
                if (quests.Count == 0) return new RunResult(true, completed);

                var (first, job) = await Game.Run(() =>
                {
                    RefuseWrongJob(quests);
                    var noPath = Unsupported(quests).Select(q => q.Id).ToHashSet();
                    var next = quests.FirstOrDefault(q => !noPath.Contains(q.Id))
                               ?? throw new ToolException($"Questionable has no path for {Join(quests.Select(q => q.Name))}.");
                    if (!IsAccepted(next) && !Ipc<string, bool>("IsReadyToAcceptQuest", next.QuestionableId))
                    {
                        var (_, reason) = Ipc<string, (bool, string)>("IsQuestLockedReason", next.QuestionableId);
                        throw new ToolException($"{next.Name} cannot be accepted yet" + (reason.Length > 0 ? $" ({reason})." : ".") + LevelWarning([next]));
                    }
                    // Quests remember the job they were accepted on, and Questionable does not switch jobs: accept on one that can do it.
                    var job = IsAccepted(next) ? Gearsets.CurrentJob() : JobFor(next)
                              ?? throw new ToolException($"No gearset is saved for a job that can do {next.Name} ({JobNames(next.Jobs)}).");
                    return (next, job);
                }).ConfigureAwait(false);

                if (job != await Game.Run(Gearsets.CurrentJob).ConfigureAwait(false))
                {
                    await StopAfterFight(ct).ConfigureAwait(false);
                    await Gearsets.Switch(await Game.Run(() => JobAbbreviation(job)).ConfigureAwait(false), ct).ConfigureAwait(false);
                }

                // Only the quests this job can do: the others come in a later round, after switching.
                var batch = await Game.Run(() =>
                {
                    var current = Gearsets.CurrentJob();
                    var mine = quests.Where(q => q.Id == first.Id || (IsAccepted(q) ? true : JobFor(q) == current)).ToList();
                    Ipc<bool>("ClearQuestPriority");
                    foreach (var q in mine) Ipc<string, bool>("AddQuestPriority", q.QuestionableId);
                    if (!IsRunning() && !Ipc<string, bool>("StartQuest", first.QuestionableId))
                        throw new ToolException($"Questionable did not start {first.Name}.");
                    return mine;
                }).ConfigureAwait(false);

                var ids = batch.Select(q => q.QuestionableId).ToHashSet();
                DateTime? stoppedSince = null, elsewhereSince = null;
                var stall = new StallWatch(StallLimit);
                var restarts = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > until) throw new ToolException($"Not done after {timeout.TotalMinutes:0} minutes; stopped while on {first.Name}.");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    var s = await Game.Run(() => (Done: batch.FirstOrDefault(Finished), Running: IsRunning(), Current: Ipc<string?>("GetCurrentQuestId"),
                                                  Position: Svc.Objects.LocalPlayer?.Position ?? default, Busy: Busy())).ConfigureAwait(false);
                    if (s.Done is { } done)
                    {
                        completed.Add(done.Name);
                        break;
                    }
                    // Questionable can get stuck (walking towards a spot in another zone after a teleport went wrong) while it still
                    // reports running: starting it again from its current step gets it going.
                    // A step for the player (a duty it cannot run, a solo duty, something by hand): Questionable waits for it.
                    if (chat.Last is { } said && QuestionableMessage.NeedsPlayer(said))
                        throw new ToolException($"Questionable waits for you: \"{QuestionableMessage.Text(said)}\" Do that step yourself (a duty also with AutoDuty), then start again.");
                    if (s.Running && stall.Stalled(DateTime.UtcNow, s.Position, s.Busy))
                    {
                        if (++restarts > MaxRestarts)
                            throw new ToolException($"Questionable made no progress on {first.Name} after {MaxRestarts} restarts." + chat.Said);
                        var again = s.Current is { Length: > 0 } cur && ids.Contains(cur) ? cur : first.QuestionableId;
                        Svc.Log.Information($"[MCP] Questionable made no progress for {StallLimit.TotalMinutes:0} minutes; restarting it on quest {again} ({restarts}/{MaxRestarts}).");
                        await StopAfterFight(ct).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                        await Game.Run(() => Ipc<string, bool>("StartQuest", again)).ConfigureAwait(false);
                        stoppedSince = null;
                        continue;
                    }
                    stoppedSince = s.Running ? null : stoppedSince ?? DateTime.UtcNow;
                    if (DateTime.UtcNow - stoppedSince > TimeSpan.FromSeconds(10))
                        throw new ToolException($"Questionable stopped on its own while on {first.Name}." + (chat.Said is { Length: > 0 } why ? why : " Its window says why (a duty, a level, a step it cannot do)."));
                    elsewhereSince = s.Current is { Length: > 0 } c && !ids.Contains(c) ? elsewhereSince ?? DateTime.UtcNow : null;
                    if (DateTime.UtcNow - elsewhereSince > TimeSpan.FromSeconds(30))
                        throw new ToolException($"Questionable turned to another quest ({s.Current}) instead of {first.Name}, so it was stopped.");
                }
            }
        }
        finally
        {
            await StopAfterFight(CancellationToken.None).ConfigureAwait(false);
            await Game.Run(() =>
            {
                if (!Loaded) return true;
                Ipc<bool>("ClearQuestPriority");
                if (QuestionablePriority.Decode(backup).Count > 0) Ipc<string, bool>("ImportQuestPriority", backup);
                return true;
            }).ConfigureAwait(false);
        }
    }

    /// <summary>How long the character may stand still, not busy, before Questionable is started again, and how often.</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(2);
    private const int MaxRestarts = 3;

    /// <summary>Busy in a way that is progress without moving: talking, fighting, a cutscene, a zone change, gathering, crafting.</summary>
    private static bool Busy()
    {
        var c = Svc.Condition;
        return c[ConditionFlag.InCombat] || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent] || c[ConditionFlag.OccupiedInCutSceneEvent]
               || c[ConditionFlag.Occupied] || c[ConditionFlag.Occupied30] || c[ConditionFlag.Occupied33] || c[ConditionFlag.Occupied38] || c[ConditionFlag.Occupied39]
               || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78] || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
               || c[ConditionFlag.Casting] || c[ConditionFlag.Gathering] || c[ConditionFlag.Crafting] || c[ConditionFlag.BoundByDuty];
    }

    /// <summary>Waits until the character is out of combat (Questionable keeps fighting), then stops Questionable. True if it waited for a fight.</summary>
    private static async Task<bool> StopAfterFight(CancellationToken ct)
    {
        var fought = false;
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < until && await Game.Run(() => Svc.Condition[ConditionFlag.InCombat]).ConfigureAwait(false))
        {
            fought = true;
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        await Game.Run(() =>
        {
            if (Loaded && IsRunning()) Ipc<string, bool>("Stop", "XIV MCP");
            return true;
        }).ConfigureAwait(false);
        return fought;
    }

    /// <summary>Questionable's chat messages while a run lasts: they say why it stops or what it waits for.</summary>
    private sealed class QuestionableChat : IDisposable
    {
        private string? last;

        public QuestionableChat() => Svc.Chat.ChatMessage += OnChat;

        public string? Last => last;

        /// <summary>" Questionable said: …", or "" when it said nothing.</summary>
        public string Said => last is { } l && QuestionableMessage.Text(l) is { } t ? $" Questionable said: \"{t}\"" : "";

        private void OnChat(IChatMessage m)
        {
            // Questionable tags its messages "[QST v…]"; with a chat type that has a sender, the tag is the sender.
            var sender = m.Sender.TextValue;
            var text = sender.StartsWith("QST", StringComparison.Ordinal) ? $"[{sender}] {m.Message.TextValue}" : m.Message.TextValue;
            if (QuestionableMessage.Text(text) is not null) last = text;
        }

        public void Dispose() => Svc.Chat.ChatMessage -= OnChat;
    }

    // ------------------------------------------------------------------ Planning (framework thread)

    private static (QuestInfo Target, QuestPlan Plan, List<QuestInfo> Alternatives) PlanQuest(string query)
    {
        var candidates = FindQuests(query);
        if (candidates.Count == 0) throw new ToolException($"No quest named '{query}'.");
        // Quests that share a name (one per starting city, or per Grand Company): the one this character can do.
        var target = candidates.Where(q => !IsComplete(q.Id)).OrderByDescending(q => IsAccepted(q) || Ready(q)).ThenByDescending(q => KnownToQuestionable(q.Id)).FirstOrDefault()
                     ?? candidates[0];
        var plan = QuestChain.Plan(target.Id, Find, IsComplete, KnownToQuestionable);
        return (target, plan, candidates.Where(q => q.Id != target.Id).ToList());
    }

    private static IReadOnlyList<QuestInfo> PlanTribe(byte tribe)
    {
        var quests = TribeQuests(tribe).Select(q => new TribeQuest(q, IsAccepted(q), !IsAccepted(q) && Ready(q)));
        return TribePlan.Next(quests, Allowances());
    }

    /// <summary>Gathering quests accepted as Fisher cannot be finished: Questionable gathers them as Miner or Botanist.</summary>
    private static void RefuseWrongJob(IEnumerable<QuestInfo> quests)
    {
        var wrong = quests.Where(q => AcceptedJob(q) is { } j && QuestJobs.AcceptedAsFisherInstead(q.Jobs, j)).ToList();
        if (wrong.Count > 0)
            throw new ToolException($"{Join(wrong.Select(q => q.Name))} {(wrong.Count == 1 ? "was" : "were")} accepted as Fisher, but Questionable only does " +
                                    $"{(wrong.Count == 1 ? "it" : "them")} as Miner or Botanist. Abandon {(wrong.Count == 1 ? "it" : "them")} in the journal and start again.");
    }

    /// <summary>The job to accept a quest on (the current one when it may), or null when no gearset fits.</summary>
    private static uint? JobFor(QuestInfo q) =>
        q.Jobs.Count == 0 ? Gearsets.CurrentJob() : QuestJobs.Pick(q.Jobs, Gearsets.CurrentJob(), GearsetJobs(), q.Level);

    private static unsafe List<JobOption> GearsetJobs()
    {
        var levels = PlayerState.Instance()->ClassJobLevels.ToArray();
        var jobs = Svc.Data.GetExcelSheet<ClassJob>();
        return Gearsets.All().Where(s => s.MissingItems == 0)
                       .Select(s => new JobOption(s.JobId, jobs.GetRowOrDefault(s.JobId) is { ExpArrayIndex: >= 0 and var i } && i < levels.Length ? levels[i] : 0))
                       .ToList();
    }

    /// <summary>The job a quest was accepted on, or null when it is not accepted.</summary>
    private static unsafe uint? AcceptedJob(QuestInfo q)
    {
        foreach (ref var w in QuestManager.Instance()->NormalQuests)
            if (w.QuestId == (ushort)(q.Id & 0xFFFF)) return w.AcceptClassJob;
        return null;
    }

    private static string JobAbbreviation(uint job) => Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(job)?.Abbreviation.ExtractText() ?? job.ToString();

    private static string JobNames(IEnumerable<uint> jobs) => string.Join(", ", jobs.Select(JobAbbreviation));

    /// <summary>The jobs a quest's ClassJobCategory allows: the sheet has a column per class and job, named by its abbreviation.</summary>
    private static readonly Dictionary<uint, List<uint>> AllowedByCategory = [];

    private static List<uint> AllowedJobs(ClassJobCategory category)
    {
        if (AllowedByCategory.TryGetValue(category.RowId, out var known)) return known;
        var allowed = new List<uint>();
        foreach (var job in Svc.Data.GetExcelSheet<ClassJob>())
        {
            if (job.RowId == 0) continue;
            if (typeof(ClassJobCategory).GetProperty(job.Abbreviation.ExtractText()) is { PropertyType: var t } p && t == typeof(bool) && (bool)p.GetValue(category)!)
                allowed.Add(job.RowId);
        }
        return AllowedByCategory[category.RowId] = allowed;
    }

    private static void Refuse(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0) throw new ToolException(string.Join(" ", problems));
    }

    /// <summary>The planned quests Questionable has no path for: added to an empty priority list, they do not show up in it.</summary>
    private static List<QuestInfo> Unsupported(IReadOnlyList<QuestInfo> quests)
    {
        var backup = Ipc<string>("ExportQuestPriority");
        try
        {
            Ipc<bool>("ClearQuestPriority");
            foreach (var q in quests) Ipc<string, bool>("AddQuestPriority", q.QuestionableId);
            var queued = QuestionablePriority.Decode(Ipc<string>("ExportQuestPriority")).ToHashSet();
            return quests.Where(q => !queued.Contains(q.QuestionableId)).ToList();
        }
        finally
        {
            Ipc<bool>("ClearQuestPriority");
            if (QuestionablePriority.Decode(backup).Count > 0) Ipc<string, bool>("ImportQuestPriority", backup);
        }
    }

    private static List<string>? Duties(IReadOnlyList<QuestInfo> quests)
    {
        var sheet = Svc.Data.GetExcelSheet<Quest>();
        var names = quests.SelectMany(q => sheet.GetRow(q.Id).InstanceContent.Where(i => i.RowId != 0)
                                                .Select(i => Svc.Data.GetExcelSheet<ContentFinderCondition>().FirstOrDefault(c => c.ContentLinkType == 1 && c.Content.RowId == i.RowId).Name.ExtractText()))
                          .Where(n => n.Length > 0).Distinct().ToList();
        return names.Count > 0 ? names : null;
    }

    private static string LevelWarning(IReadOnlyList<QuestInfo> quests)
    {
        var level = (int)(Svc.Objects.LocalPlayer?.Level ?? 0);
        var high = quests.FirstOrDefault(q => q.Level > level);
        return high is null ? "" : $" {high.Name} needs level {high.Level}; the current job is level {level}.";
    }

    // ------------------------------------------------------------------ Game data and state (framework thread)

    private static QuestInfo? Find(uint id) => Svc.Data.GetExcelSheet<Quest>().GetRowOrDefault(id) is { } q && q.RowId != 0 ? ToInfo(q) : null;

    private static QuestInfo ToInfo(Quest q) => new(q.RowId, Game.Clean(q.Name.ExtractText()) ?? $"Quest {q.RowId & 0xFFFF}")
    {
        Previous = q.PreviousQuest.Select(p => p.RowId).ToList(),
        PreviousJoin = (QuestJoin)q.PreviousQuestJoin,
        Locks = q.QuestLock.Select(p => p.RowId).ToList(),
        LockJoin = (QuestJoin)q.QuestLockJoin,
        Level = q.ClassJobLevel[0],
        MainScenario = q.JournalGenre.ValueNullable?.Icon == MainScenarioIcon,
        Society = (byte)q.BeastTribe.RowId,
        SocietyRank = (byte)q.BeastReputationRank.RowId,
        Repeatable = q.IsRepeatable,
        Jobs = q.ClassJobCategory0.ValueNullable is { } c && c.RowId > 1 ? AllowedJobs(c) : [],
    };

    /// <summary>Quests by id (the sheet row, or the quest number) or name: exact names first, else names containing the text.</summary>
    private static List<QuestInfo> FindQuests(string query)
    {
        var sheet = Svc.Data.GetExcelSheet<Quest>();
        if (uint.TryParse(query.Trim(), out var id))
            return Find(id < 65536 ? id + 65536 : id) is { } byId ? [byId] : [];
        var named = sheet.Where(q => q.Name.ExtractText().Length > 0).Select(q => (Quest: q, Name: Game.Clean(q.Name.ExtractText()) ?? "")).ToList();
        var exact = named.Where(n => n.Name.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return (exact.Count > 0 ? exact : named.Where(n => Game.Matches(n.Name, query.Trim())).Take(20).ToList()).Select(n => ToInfo(n.Quest)).ToList();
    }

    private sealed record Tribe(byte Id, string Name);

    private static Tribe FindTribe(string query)
    {
        var tribes = Svc.Data.GetExcelSheet<BeastTribe>().Where(t => t.RowId != 0)
                        .Select(t => new Tribe((byte)t.RowId, Game.TitleCase(t.Name.ExtractText()))).ToList();
        query = query.Trim();
        if (byte.TryParse(query, out var id) && tribes.FirstOrDefault(t => t.Id == id) is { } byId) return byId;
        // "Sylph" for "sylphs", "Moogle" for "moogles".
        return tribes.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
               ?? tribes.FirstOrDefault(t => Game.Matches(t.Name, query) || Game.Matches(query, t.Name.TrimEnd('s')))
               ?? throw new ToolException($"No allied society named '{query}'. They are: {string.Join(", ", tribes.Select(t => t.Name))}.");
    }

    private static IEnumerable<QuestInfo> TribeQuests(byte tribe) =>
        Svc.Data.GetExcelSheet<Quest>().Where(q => q.BeastTribe.RowId == tribe && q.Name.ExtractText().Length > 0).Select(ToInfo);

    /// <summary>The first quest of an allied society: the one that unlocks it.</summary>
    private static QuestInfo? TribeUnlockQuest(byte tribe) =>
        TribeQuests(tribe).Where(q => !q.Repeatable && !IsComplete(q.Id)).OrderBy(q => q.SocietyRank).ThenBy(q => q.Id).FirstOrDefault();

    private sealed record Reputation(int Rank, string RankName, int Value, int Needed);

    private static unsafe Reputation TribeRank(byte tribe)
    {
        var ps = PlayerState.Instance();
        var rank = ps->GetBeastTribeRank(tribe);
        return new Reputation(rank, Svc.Data.GetExcelSheet<BeastReputationRank>().GetRowOrDefault(rank)?.Name.ExtractText() ?? "",
                              ps->GetBeastTribeCurrentReputation(tribe), ps->GetBeastTribeNeededReputation(tribe));
    }

    private static unsafe int Allowances() => (int)QuestManager.Instance()->GetBeastTribeAllowance();

    private static bool IsComplete(uint id) => QuestManager.IsQuestComplete((ushort)(id & 0xFFFF));

    private static unsafe bool IsAccepted(QuestInfo q) => QuestManager.Instance()->IsQuestAccepted((ushort)(q.Id & 0xFFFF));

    /// <summary>A planned quest that is done: completed, or for a daily, completed today.</summary>
    private static unsafe bool Finished(QuestInfo q) =>
        q.Repeatable ? QuestManager.Instance()->IsDailyQuestCompleted((ushort)(q.Id & 0xFFFF)) && !IsAccepted(q) : IsComplete(q.Id);

    private static bool Ready(QuestInfo q) => Ipc<string, bool>("IsReadyToAcceptQuest", q.QuestionableId);

    /// <summary>Whether Questionable has a path for a quest: for quests it does not know, it reports them locked without a reason.</summary>
    private static bool KnownToQuestionable(uint id)
    {
        var questionableId = (id & 0xFFFF).ToString();
        var (locked, reason) = Ipc<string, (bool, string)>("IsQuestLockedReason", questionableId);
        return !locked || reason.Length > 0;
    }

    private static unsafe QuestInfo? NextMainScenarioQuest()
    {
        var tree = AgentScenarioTree.Instance();
        if (tree == null || tree->Data == null) return null;
        var id = tree->Data->MainScenarioQuestIds[0];
        return id == 0 ? null : Find(id + 65536u);
    }

    private static object Describe(QuestInfo q) => new
    {
        questId = q.Id,
        name = q.Name,
        level = q.Level,
        mainScenario = q.MainScenario ? true : (bool?)null,
        daily = q.Repeatable ? true : (bool?)null,
        rankUp = q.Society != 0 && !q.Repeatable ? true : (bool?)null,
    };

    private static object Status()
    {
        if (!Loaded) return new { loaded = false };
        var current = Ipc<string?>("GetCurrentQuestId");
        var priority = QuestionablePriority.Decode(Ipc<string>("ExportQuestPriority"));
        var tribes = Svc.Data.GetExcelSheet<BeastTribe>().Where(t => t.RowId != 0)
                        .Select(t => (Name: Game.TitleCase(t.Name.ExtractText()), Rep: TribeRank((byte)t.RowId)))
                        .Where(t => t.Rep.Rank > 0)
                        .Select(t => new { tribe = t.Name, rank = t.Rep.RankName, reputation = t.Rep.Value, needed = t.Rep.Needed });
        return new
        {
            loaded = true,
            running = IsRunning(),
            currentQuest = current is { Length: > 0 } && ushort.TryParse(current, out var c) && Find(c + 65536u) is { } cq ? Describe(cq) : current,
            priorityList = priority.Select(p => ushort.TryParse(p, out var n) && Find(n + 65536u) is { } q ? q.Name : p),
            nextMainScenarioQuest = NextMainScenarioQuest() is { } msq ? Describe(msq) : null,
            alliedSocietyAllowances = Allowances(),
            alliedSocieties = tribes,
        };
    }

    // ------------------------------------------------------------------ Questionable

    internal static bool Loaded => PluginCompat.IsLoaded(PluginId);

    private static void RequireLoaded()
    {
        if (!Loaded) throw new ToolException("Questionable is not loaded.");
    }

    private static bool IsRunning() => Ipc<bool>("IsRunning");

    private static T Ipc<T>(string name) => Svc.PluginInterface.GetIpcSubscriber<T>($"Questionable.{name}").InvokeFunc();

    private static TResult Ipc<TArg, TResult>(string name, TArg arg) =>
        Svc.PluginInterface.GetIpcSubscriber<TArg, TResult>($"Questionable.{name}").InvokeFunc(arg);

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }
}
