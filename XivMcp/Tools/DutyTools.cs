using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Dungeons with AutoDuty: list the duties it can run, run one (or loop it until items / a currency reach a target) and stop it.
/// The tools only exist while AutoDuty is loaded. run_duty is meant as a background job step (start_job): a run takes ~20 minutes.
/// </summary>
internal static class DutyTools
{
    private static readonly string[] Modes = ["Support", "Trust", "Squadron", "Regular", "Trial", "Raid", "Variant"];

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        void RequireEnabled()
        {
            if (!config.AllowGameNavigation)
                throw new ToolException("Running duties is disabled. Enable \"Game & navigation\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        static bool Available() => AutoDutyBridge.Loaded;

        yield return new McpTool
        {
            Name = "list_duties",
            Description = "Duties AutoDuty can run (it has a path for them), with level / item level requirement and the modes it can run them in " +
                          "(Support, Trust, Squadron, Regular, …). Filter by name, mode or level. Only available while AutoDuty is loaded.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Part of the duty name." },
                    "mode": { "type": "string", "enum": ["Support", "Trust", "Squadron", "Regular", "Trial", "Raid", "Variant"] },
                    "max_level": { "type": "integer", "description": "Only duties with at most this level requirement." },
                    "include_without_path": { "type": "boolean", "description": "Also list duties AutoDuty knows but has no path for (default false)." },
                    "limit": { "type": "integer", "description": "Max results (default 50)." }
                  }
                }
                """,
            Available = Available,
            Handler = async (args, _) =>
            {
                var query = args.String("query");
                var mode = args.String("mode");
                var maxLevel = args.Int("max_level", int.MaxValue, 1);
                var all = args.Bool("include_without_path", false);
                var limit = args.Int("limit", 50, 1, 500);
                var duties = await Game.Run(AutoDutyBridge.Duties).ConfigureAwait(false);
                var matching = duties
                    .Where(d => all || d.HasPath)
                    .Where(d => query is null || Game.Matches(d.Name, query))
                    .Where(d => mode is null || d.Modes.Contains(mode, StringComparer.OrdinalIgnoreCase))
                    .Where(d => d.Level <= maxLevel)
                    .ToList();
                return new
                {
                    total = matching.Count,
                    duties = matching.Take(limit).Select(d => new
                    {
                        name = d.Name, territoryType = d.TerritoryType, level = d.Level, itemLevel = d.ItemLevel, modes = d.Modes, hasPath = d.HasPath,
                    }).ToList(),
                };
            },
        };

        yield return new McpTool
        {
            Name = "get_duty_status",
            Description = "What AutoDuty is doing: looping or stopped, its stage and current action, the duty and the loop count. " +
                          "Only available while AutoDuty is loaded.",
            Available = Available,
            Handler = async (_, _) =>
            {
                var s = await Game.Run(AutoDutyBridge.Status).ConfigureAwait(false);
                return new
                {
                    running = s.Looping || !s.Stopped,
                    looping = s.Looping,
                    stage = s.Stage,
                    action = string.IsNullOrEmpty(s.Action) ? null : s.Action,
                    duty = s.Duty,
                    loop = s.CurrentLoop,
                    loopTimes = s.LoopTimes,
                    inDuty = s.DutyTerritory is { } t && t == Svc.ClientState.TerritoryType,
                };
            },
        };

        yield return new McpTool
        {
            Name = "leave_duty",
            Description = "Leaves the current duty like the Duty Finder's Leave entry. If a dungeon automation plugin is running the duty, it keeps " +
                          "fighting until you are out of combat, then it is stopped and the duty left right away (never mid-fight). Needs 'Game & navigation' in /xivmcp.",
            ReadOnly = false,
            Handler = async (_, ct) =>
            {
                RequireEnabled();
                if (!await Game.RunLoggedIn(DutyExit.InDuty).ConfigureAwait(false)) throw new ToolException("You are not in a duty.");
                if (AutoDutyBridge.Loaded && await Game.Run(AutoDutyBridge.Running).ConfigureAwait(false))
                {
                    if (!await StopSafely(true, TimeSpan.FromMinutes(10), ct, ownsOverrides: false).ConfigureAwait(false))
                        throw new ToolException("AutoDuty was stopped, but leaving the duty did not work.");
                }
                else await DutyExit.Leave(TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
                return "Left the duty.";
            },
        };

        yield return new McpTool
        {
            Name = "stop_duty",
            Description = "Stops AutoDuty, but never mid-fight: it keeps fighting until you are out of combat (it may leave you inside the duty; leave_duty leaves it). A running run_duty job step is stopped with pause_job / cancel_job instead. " +
                          "Only available while AutoDuty is loaded.",
            ReadOnly = false,
            Available = Available,
            Handler = async (_, ct) =>
            {
                if (!await Game.Run(AutoDutyBridge.Running).ConfigureAwait(false)) return "AutoDuty is not running.";
                await StopSafely(false, TimeSpan.FromMinutes(10), ct, ownsOverrides: false).ConfigureAwait(false);
                return "AutoDuty stopped (after the fight, if one was going on).";
            },
        };

        yield return new McpTool
        {
            Name = "run_duty",
            Description = "Runs a duty with AutoDuty, in a loop: 'loops' times, or — with 'until' — until the inventory holds the wanted items or " +
                          "currency (e.g. a dungeon drop, or tomestones), at most 'loops' runs. AutoDuty does the running and the looping; its " +
                          "loop settings (loop count, duty mode, unsynced, stop conditions) are set for this run only and restored afterwards, and " +
                          "its own termination action is set to do nothing. 'until' entries: { item, quantity } = how many you want to have, or " +
                          "{ item, gain } = how many more than now. Returns the runs done and the counts; fails (job: pending) if AutoDuty stops " +
                          "before the target or the loop count is reached. A run takes ~20 minutes: use it as a job step (start_job). Pausing or " +
                          "cancelling the job stops AutoDuty. Only available while AutoDuty is loaded; needs 'Game & navigation' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "duty": { "type": "string", "description": "Duty name (or territory id), see list_duties." },
                    "mode": { "type": "string", "enum": ["Support", "Trust", "Squadron", "Regular", "Trial", "Raid", "Variant"], "description": "Duty mode (default: AutoDuty's current one if the duty supports it, else Support, Trust, Regular)." },
                    "loops": { "type": "integer", "description": "Runs to do; with 'until' the maximum number of runs (default 1, or 99 with 'until')." },
                    "until": { "type": "array", "items": { "type": "object", "properties": { "item": { "type": "string" }, "quantity": { "type": "integer" }, "gain": { "type": "integer" } }, "required": ["item"] } },
                    "until_all": { "type": "boolean", "description": "With several 'until' entries: stop when all are reached (default true) or as soon as one is." },
                    "unsynced": { "type": "boolean", "description": "Run unsynced (AutoDuty's setting is used if omitted)." },
                    "gearset": { "type": "string", "description": "Switch to this gearset first (number, name or job, see list_gearsets). Needed when you're on a crafter or gatherer." },
                    "timeout_minutes": { "type": "integer", "description": "Give up (and stop AutoDuty) after this long (default 45 per run, max 24 h)." }
                  },
                  "required": ["duty"]
                }
                """,
            ReadOnly = false,
            Available = Available,
            Handler = async (args, ct) =>
            {
                var dutyArg = args.String("duty") ?? throw new ToolException("Give 'duty'.");
                var modeArg = args.String("mode");
                var untilAll = args.Bool("until_all", true);
                var untilNode = args.Node("until") as JsonArray;
                bool? unsynced = args.Node("unsynced") is null ? null : args.Bool("unsynced", false);
                var gearsetArg = args.String("gearset");

                // A duty needs a combat job: switch first, or refuse with the gearsets that would do.
                Gearsets.Set? switchedTo = null;
                if (gearsetArg is not null)
                {
                    await Game.RunLoggedIn(() =>
                    {
                        if (AutoDutyBridge.IsLooping() || !AutoDutyBridge.IsStopped())
                            throw new ToolException("AutoDuty is already running; stop it first (stop_duty) or wait for it.");
                        if (!Gearsets.Resolve(gearsetArg).CombatJob) throw new ToolException($"Gearset '{gearsetArg}' is not a combat job.");
                        return true;
                    }).ConfigureAwait(false);
                    switchedTo = await Gearsets.Switch(gearsetArg, ct).ConfigureAwait(false);
                }
                else
                    await Game.RunLoggedIn(() =>
                    {
                        if (Gearsets.IsCombatJob(Gearsets.CurrentJob())) return true;
                        var combat = Gearsets.All().Where(s => s.CombatJob).OrderByDescending(s => s.ItemLevel).Take(10).Select(s => $"{s.Number} {s.Name} ({s.Job}, i{s.ItemLevel})");
                        throw new ToolException($"You are on a crafter or gatherer; give 'gearset' to switch to a combat job first. Combat gearsets: {string.Join(", ", combat)}.");
                    }).ConfigureAwait(false);

                // Resolve the duty, mode and the targets on the framework thread.
                var plan = await Game.RunLoggedIn(() =>
                {
                    AutoDutyBridge.Require();
                    if (AutoDutyBridge.IsLooping() || !AutoDutyBridge.IsStopped())
                        throw new ToolException("AutoDuty is already running; stop it first (stop_duty) or wait for it.");
                    var duty = ResolveDuty(AutoDutyBridge.Duties(), dutyArg);
                    if (!duty.HasPath) throw new ToolException($"AutoDuty has no path for {duty.Name}.");
                    var current = AutoDutyBridge.GetConfig("DutyModeEnum");
                    string mode;
                    if (modeArg is not null)
                    {
                        mode = Modes.First(m => m.Equals(modeArg, StringComparison.OrdinalIgnoreCase));
                        if (!duty.Modes.Contains(mode)) throw new ToolException($"{duty.Name} can't be run in {mode} mode; it supports: {string.Join(", ", duty.Modes)}.");
                    }
                    else
                        mode = new[] { current, "Support", "Trust", "Regular" }.FirstOrDefault(m => m is not null && duty.Modes.Contains(m))
                               ?? duty.Modes.FirstOrDefault() ?? throw new ToolException($"AutoDuty can't run {duty.Name} in any mode.");

                    var targets = new Dictionary<uint, int>();
                    var start = new Dictionary<uint, int>();
                    foreach (var o in (untilNode ?? []).OfType<JsonObject>())
                    {
                        var a = new ToolArgs(o);
                        var item = Items.Resolve(a.String("item") ?? throw new ToolException("Each 'until' entry needs 'item'."));
                        var have = Have(item.RowId);
                        var want = a.Node("quantity") is not null ? a.Int("quantity", 1, 1)
                                 : a.Node("gain") is not null ? have + a.Int("gain", 1, 1)
                                 : throw new ToolException($"Give 'quantity' or 'gain' for {item.Name.ExtractText()}.");
                        if (Items.IsCurrency(item.RowId))
                        {
                            // The currency manager knows the caps of scrips and the like; for others (tomestones) the stack size is the cap.
                            var cap = Collectables.CurrencyRoom(item.RowId).Max is > 0 and var m ? m : Items.StackSize(item.RowId);
                            if (cap > 1 && want > cap) throw new ToolException($"{item.Name.ExtractText()} caps at {cap:N0}; a target of {want:N0} can't be reached.");
                        }
                        targets[item.RowId] = want;
                        start[item.RowId] = have;
                    }
                    return (Duty: duty, Mode: mode, Targets: targets, Start: start);
                }).ConfigureAwait(false);

                var targets = plan.Targets;
                var loops = args.Int("loops", targets.Count > 0 ? 99 : 1, 1, 999);
                var timeout = TimeSpan.FromMinutes(args.Int("timeout_minutes", Math.Min(24 * 60, 45 * loops), 1, 24 * 60));
                bool Met(Dictionary<uint, int> have) =>
                    targets.Count > 0 && (untilAll ? targets.All(t => have[t.Key] >= t.Value) : targets.Any(t => have[t.Key] >= t.Value));
                Dictionary<uint, int> Counts() => targets.ToDictionary(t => t.Key, t => Have(t.Key));

                if (targets.Count > 0 && await Game.Run(() => Met(Counts())).ConfigureAwait(false))
                    return new { done = true, runs = 0, note = "The targets are already reached.", items = Describe(targets, plan.Start, await Game.Run(Counts).ConfigureAwait(false)) };

                // AutoDuty's loop settings for this run; all reverted by PopOverrides.
                List<DictionaryEntry>? previousStopItems = null;
                await Game.Run(() =>
                {
                    var required = new List<(string, string)>
                    {
                        ("AutoDutyModeEnum", "Looping"), ("LoopTimes", loops.ToString()), ("DutyModeEnum", plan.Mode),
                        ("EnableTerminationActions", "true"), ("StopItemQty", targets.Count > 0 ? "true" : "false"),
                        ("StopItemAll", untilAll ? "true" : "false"), ("TerminationMethodEnum", "Do_Nothing"),
                    };
                    if (unsynced is { } u) required.Add(("Unsynced", u ? "true" : "false"));
                    foreach (var (k, v) in required)
                        if (!AutoDutyBridge.Override(k, v))
                        {
                            AutoDutyBridge.PopOverrides();
                            throw new ToolException($"AutoDuty rejected the setting {k}={v}; this AutoDuty version may not be supported.");
                        }
                    // Only our conditions end the loop (AutoDuty's own inventory-full stop stays as the player set it).
                    foreach (var k in new[] { "StopLevel", "StopNoRestedXP", "StopWhenDutyGathered", "TerminationiLvl", "TerminationBLUSpellsEnabled" })
                        AutoDutyBridge.Override(k, "false");
                    try { if (targets.Count > 0) previousStopItems = AutoDutyBridge.SetStopItems(targets); }
                    catch { AutoDutyBridge.PopOverrides(); throw; }
                    return true;
                }).ConfigureAwait(false);

                var started = DateTime.UtcNow;
                var runsStarted = 0;
                var runsDone = 0;
                var stopReason = "";
                try
                {
                    await Game.Run(() => { AutoDutyBridge.Run(plan.Duty.TerritoryType, loops); return true; }).ConfigureAwait(false);

                    var wasInDuty = false;
                    var seenRunning = false;
                    var stoppedPolls = 0;
                    DateTime? metOutsideSince = null;
                    while (true)
                    {
                        await Task.Delay(seenRunning ? 5000 : 2000, ct).ConfigureAwait(false);
                        var s = await Game.Run(() => (Looping: AutoDutyBridge.IsLooping(), Stopped: AutoDutyBridge.IsStopped(),
                                                      InDuty: Svc.ClientState.TerritoryType == plan.Duty.TerritoryType, Have: Counts())).ConfigureAwait(false);
                        if (s.InDuty && !wasInDuty) runsStarted++;
                        if (!s.InDuty && wasInDuty) runsDone++;
                        wasInDuty = s.InDuty;

                        var running = s.Looping || !s.Stopped;
                        if (running) { seenRunning = true; stoppedPolls = 0; }
                        else if (!seenRunning && DateTime.UtcNow - started > TimeSpan.FromSeconds(60))
                            throw new ToolException($"AutoDuty did not start {plan.Duty.Name} (check its window / log for why).");
                        else if (seenRunning && ++stoppedPolls >= 3) break; // stopped for ~15 s: the loop is over

                        // AutoDuty checks the stop conditions after each run; should it miss one (e.g. a currency it doesn't count), stop it between runs.
                        if (Met(s.Have) && !s.InDuty && running)
                        {
                            metOutsideSince ??= DateTime.UtcNow;
                            if (DateTime.UtcNow - metOutsideSince > TimeSpan.FromSeconds(45))
                            {
                                await Game.Run(() => { AutoDutyBridge.Stop(); return true; }).ConfigureAwait(false);
                                stopReason = "stopped by XIV MCP: target reached";
                            }
                        }
                        else metOutsideSince = null;

                        if (DateTime.UtcNow - started > timeout)
                        {
                            var left = await StopAndLeaveAfterRun().ConfigureAwait(false);
                            throw new ToolException($"Timed out after {timeout.TotalMinutes:N0} minutes ({runsDone} runs done); AutoDuty was stopped{(left ? " and the duty left" : "")}.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelled or paused: stop AutoDuty once out of combat and leave the duty, rather than leaving the player standing in it.
                    await StopAndLeaveAfterRun().ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    await Game.Run(() =>
                    {
                        try { if (previousStopItems is not null) AutoDutyBridge.RestoreStopItems(previousStopItems); }
                        catch (Exception ex) { Svc.Log.Warning($"[MCP] Could not restore AutoDuty's item stop list: {ex.Message}"); }
                        AutoDutyBridge.PopOverrides();
                        return true;
                    }).ConfigureAwait(false);
                }

                var have = await Game.Run(Counts).ConfigureAwait(false);
                var minutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1);
                var inDuty = await Game.Run(() => Svc.ClientState.TerritoryType == plan.Duty.TerritoryType).ConfigureAwait(false);
                if (targets.Count > 0)
                {
                    if (!Met(have))
                        throw new ToolException($"AutoDuty stopped after {runsDone} of at most {loops} runs of {plan.Duty.Name} before the target was reached " +
                                                $"({string.Join(", ", targets.Select(t => $"{Items.Name(t.Key)} {have[t.Key]:N0}/{t.Value:N0}"))})" +
                                                (inDuty ? "; you are still inside the duty." : "."));
                    return new { done = true, duty = plan.Duty.Name, mode = plan.Mode, gearset = switchedTo?.Name, runs = runsDone, minutes, items = Describe(targets, plan.Start, have), note = stopReason == "" ? null : stopReason };
                }
                if (runsDone < loops)
                    throw new ToolException($"AutoDuty stopped after {runsDone} of {loops} runs of {plan.Duty.Name}" + (inDuty ? "; you are still inside the duty." : "."));
                return new { done = true, duty = plan.Duty.Name, mode = plan.Mode, gearset = switchedTo?.Name, runs = runsDone, minutes };
            },
        };
    }

    /// <summary>
    /// Stops AutoDuty without leaving the player defenceless: it keeps fighting until the player has been out of combat for a few
    /// seconds, and only then is stopped (and, with <paramref name="leave"/>, the duty left right away). If a fight starts again before
    /// the player is out (a patrol, an add), AutoDuty is resumed for that fight and stopping is tried again afterwards. If the player is
    /// still in combat at the timeout, AutoDuty is left running. Returns true if a duty was left.
    /// <paramref name="ownsOverrides"/>: false when no run_duty overrides are active, so the loop-count override this may push for a
    /// resume is popped here.
    /// </summary>
    private static async Task<bool> StopSafely(bool leave, TimeSpan timeout, CancellationToken ct, bool ownsOverrides)
    {
        var until = DateTime.UtcNow + timeout;
        DateTime? calmSince = null;
        var pushed = false;
        try
        {
            while (DateTime.UtcNow < until)
            {
                var s = await Game.Run(() => (Combat: Svc.Condition[ConditionFlag.InCombat], InDuty: DutyExit.InDuty(),
                                              Running: AutoDutyBridge.Loaded && AutoDutyBridge.Running())).ConfigureAwait(false);
                if (s.Combat)
                {
                    calmSince = null;
                    if (!s.Running && s.InDuty && AutoDutyBridge.Loaded)
                    {
                        // We stopped it and a fight started: let AutoDuty fight it (one loop, from where the path is).
                        await Game.Run(() =>
                        {
                            if (!pushed) { AutoDutyBridge.Override("AutoDutyModeEnum", "Looping"); AutoDutyBridge.Override("LoopTimes", "1"); pushed = true; }
                            AutoDutyBridge.Resume();
                            return true;
                        }).ConfigureAwait(false);
                        Svc.Log.Information("[MCP] Combat started after stopping AutoDuty; resumed it until the fight is over.");
                    }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }
                if (!s.InDuty && !s.Running) return false;
                calmSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - calmSince < TimeSpan.FromSeconds(3))
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }
                // Out of combat for 3 s: stop in the same frame as the check.
                var stopped = await Game.Run(() =>
                {
                    if (Svc.Condition[ConditionFlag.InCombat]) return false;
                    if (AutoDutyBridge.Loaded && AutoDutyBridge.Running()) AutoDutyBridge.Stop();
                    return true;
                }).ConfigureAwait(false);
                if (!stopped) continue;
                if (!leave || !s.InDuty) return false;
                try { return await DutyExit.Leave(TimeSpan.FromSeconds(30), ct, abortOnCombat: true).ConfigureAwait(false); }
                catch (ToolException ex) { Svc.Log.Information($"[MCP] {ex.Message} Retrying after the fight."); calmSince = null; }
            }
            throw new ToolException("Still in combat at the time limit, so AutoDuty was left running (stopping it mid-fight could get you killed).");
        }
        finally
        {
            if (pushed && !ownsOverrides) await Game.Run(() => { AutoDutyBridge.PopOverrides(); return true; }).ConfigureAwait(false);
        }
    }

    /// <summary>run_duty's cancel / timeout path: stop safely and leave; never throws. True if a duty was left.</summary>
    private static async Task<bool> StopAndLeaveAfterRun()
    {
        try { return await StopSafely(true, TimeSpan.FromMinutes(60), CancellationToken.None, ownsOverrides: true).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[MCP] Stopping AutoDuty / leaving the duty: {ex.Message}");
            return false;
        }
    }

    private static AutoDutyBridge.Duty ResolveDuty(List<AutoDutyBridge.Duty> duties, string query)
    {
        if (uint.TryParse(query, out var id))
            return duties.FirstOrDefault(d => d.TerritoryType == id) ?? duties.FirstOrDefault(d => d.ContentFinderCondition == id)
                   ?? throw new ToolException($"AutoDuty doesn't know a duty with territory {id}.");
        var exact = duties.Where(d => d.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || d.Name.Equals("The " + query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact.OrderByDescending(d => d.HasPath).First();
        var partial = duties.Where(d => Game.Matches(d.Name, query)).ToList();
        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new ToolException($"AutoDuty doesn't know a duty matching '{query}'. See list_duties."),
            _ => throw new ToolException($"'{query}' matches several duties: {string.Join(", ", partial.Take(8).Select(d => d.Name))}. Be more specific."),
        };
    }

    /// <summary>The count AutoDuty's stop check sees, or the currency manager's for currencies if that is higher. Framework thread.</summary>
    private static int Have(uint itemId)
    {
        var count = AutoDutyBridge.Count(itemId);
        if (Items.IsCurrency(itemId)) count = (int)Math.Max(count, Collectables.CurrencyRoom(itemId).Have);
        return count;
    }

    private static object Describe(Dictionary<uint, int> targets, Dictionary<uint, int> start, Dictionary<uint, int> have) =>
        targets.Select(t => new { item = Items.Name(t.Key), target = t.Value, have = have[t.Key], gained = have[t.Key] - start[t.Key] }).ToList();
}
