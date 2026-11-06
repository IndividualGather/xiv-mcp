using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using XivMcp.Crafting;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Tools that run until their work is done — meant as background job steps (start_job), where they may take hours. Called directly
/// they work too, within the client's own time limit. Cancelling (pause_job / cancel_job) stops the underlying plugin cleanly.
/// </summary>
internal static class LongRunningTools
{
    private const string TempGatherList = "MCP job";
    private const string TempFallbackList = "MCP job fallback";

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        void RequireEnabled()
        {
            if (!config.AllowCraftingGathering)
                throw new ToolException("Crafting & gathering automation is disabled. Enable it in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "gather_until",
            Description = "Gathers with GatherBuddy Reborn until the bags hold the wanted quantities, then stops. Give 'items' ([{ item, quantity }] " +
                          "— quantity = how many you want to have in the bags) or an existing GatherBuddy 'list' (its quantities are the targets). " +
                          "'until' ([{ item, quantity }]) stops on other items instead, e.g. the aethersand GatherBuddy reduces gathered " +
                          "collectables into (the gathered items then never stay in the bags): give the collectables in 'items' with a high " +
                          "quantity and the aethersand in 'until'. Other active auto-gather lists are switched off meanwhile and restored " +
                          "afterwards. When GatherBuddy switches itself off before the targets are reached (e.g. between the windows of timed " +
                          "nodes), it is started again, up to 'restarts' times (default 20). Fails (job: pending) when that runs out, or the bags " +
                          "are full. Meant as a job step (start_job) — it can run for hours. Requires GatherBuddy Reborn and 'Crafting & " +
                          "gathering' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "items": { "type": "array", "items": { "type": "object", "properties": { "item": { "type": "string" }, "quantity": { "type": "integer" } }, "required": ["item", "quantity"] } },
                    "list": { "type": "string", "description": "Existing GatherBuddy list (name or index) instead of items." },
                    "fallback": { "type": "array", "description": "Items GatherBuddy only gathers while nothing from 'items' can be (its fallback list), e.g. a fish between the windows of timed nodes. Quantity defaults to 9999.", "items": { "type": "object", "properties": { "item": { "type": "string" }, "quantity": { "type": "integer" } }, "required": ["item"] } },
                    "until": { "type": "array", "description": "Stop once the bags hold these instead of the gathered items' quantities.", "items": { "type": "object", "properties": { "item": { "type": "string" }, "quantity": { "type": "integer" } }, "required": ["item", "quantity"] } },
                    "restarts": { "type": "integer", "minimum": 0, "maximum": 200, "description": "How often to start GatherBuddy again when it switches itself off (default 20)." },
                    "stall_minutes": { "type": "integer", "minimum": 0, "maximum": 240, "description": "Fail when the character stands idle this long with no count changing while GatherBuddy claims to gather (default 6; 0 turns the check off). Waiting for a timed node never counts." },
                    "session_minutes": { "type": "integer", "minimum": 1, "description": "Gather for at most this long, then stop without failing (done=false), e.g. before a break; a later gather_until step carries on." },
                    "timeout_minutes": { "type": "integer", "description": "Give up after this long (default 600)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var timeout = TimeSpan.FromMinutes(args.Int("timeout_minutes", 600, 1, 24 * 60));
                await Game.RunLoggedIn(() => { CraftGatherTools.RequireGatherBuddy(); InventoryActionTools.EnsureNotBusy(); return true; }).ConfigureAwait(false);

                // Targets and the list GatherBuddy should work on.
                var listArg = args.String("list");
                var items = args.Node("items") as JsonArray;
                if (listArg is null && (items is null || items.Count == 0)) throw new ToolException("Give 'items' or 'list'.");
                var targets = new Dictionary<uint, int>();
                if (items is not null)
                    await Game.Run(() =>
                    {
                        foreach (var o in items.OfType<JsonObject>())
                        {
                            var a = new ToolArgs(o);
                            targets[CraftGatherTools.ResolveGatherable(a.String("item") ?? throw new ToolException("Each item needs 'item'."))] = a.Int("quantity", 1, 1, 99999);
                        }
                        return true;
                    }).ConfigureAwait(false);
                var maxRestarts = args.Int("restarts", 20, 0, 200);
                var stallWindow = TimeSpan.FromMinutes(args.Int("stall_minutes", (int)GatherStallWatch.DefaultWindow.TotalMinutes, 0, 240));
                TimeSpan? session = args.Node("session_minutes") is null ? null : TimeSpan.FromMinutes(args.Int("session_minutes", 60, 1, 24 * 60));
                Dictionary<uint, int>? until = null;
                if (args.Node("until") is JsonArray untilItems && untilItems.Count > 0)
                    until = await Game.Run(() => untilItems.OfType<JsonObject>().Select(o => new ToolArgs(o)).ToDictionary(
                        a => Items.Resolve(a.String("item") ?? throw new ToolException("Each 'until' entry needs 'item'.")).RowId,
                        a => a.Int("quantity", 1, 1, 99999))).ConfigureAwait(false);

                // Fallback items go into a list of their own that GatherBuddy only works on while nothing from the main list can be
                // gathered (e.g. a fish between the windows of timed nodes).
                var fallback = new Dictionary<uint, int>();
                if (args.Node("fallback") is JsonArray fallbackItems && fallbackItems.Count > 0)
                    await Game.Run(() =>
                    {
                        foreach (var a in fallbackItems.OfType<JsonObject>().Select(o => new ToolArgs(o)))
                            fallback[CraftGatherTools.ResolveGatherable(a.String("item") ?? throw new ToolException("Each 'fallback' entry needs 'item'."))] = a.Int("quantity", 9999, 1, 99999);
                        return true;
                    }).ConfigureAwait(false);

                List<string> restore = [];
                string? usedList = null;
                await PluginTools.ModifyPluginJson(CraftGatherTools.GatherBuddy, CraftGatherTools.GatherListsFile, root =>
                {
                    var lists = root as JsonArray ?? throw new ToolException("GatherBuddy's list file has an unexpected format.");
                    JsonObject list;
                    if (listArg is not null)
                    {
                        list = CraftGatherTools.MatchGatherList(lists, listArg) ?? throw new ToolException($"No GatherBuddy list '{listArg}'.");
                        foreach (var id in (list["ItemIds"] as JsonArray ?? []).Select(n => n!.GetValue<uint>()))
                            if (list["EnabledItems"]?[id.ToString()]?.GetValue<bool>() != false)
                                targets[id] = list["Quantities"]?[id.ToString()]?.GetValue<int>() ?? 1;
                    }
                    else
                    {
                        list = lists.OfType<JsonObject>().FirstOrDefault(l => l["Name"]?.ToString() == TempGatherList) ?? new JsonObject
                        {
                            ["PrefferedLocations"] = new JsonObject(), ["Name"] = TempGatherList, ["Description"] = "Created by an XIV MCP job; removed when done.",
                            ["FolderPath"] = "MCP", ["Order"] = lists.Count, ["Fallback"] = false, ["RemoveCompletedItems"] = false,
                        };
                        if (!lists.Contains(list)) lists.Add(list);
                        list["ItemIds"] = new JsonArray(targets.Keys.Select(k => (JsonNode)JsonValue.Create(k)).ToArray());
                        list["Quantities"] = new JsonObject(targets.Select(t => new KeyValuePair<string, JsonNode?>(t.Key.ToString(), JsonValue.Create(t.Value))));
                        list["EnabledItems"] = new JsonObject(targets.Select(t => new KeyValuePair<string, JsonNode?>(t.Key.ToString(), JsonValue.Create(true))));
                    }
                    usedList = list["Name"]?.ToString();
                    JsonObject? fallbackList = null;
                    if (fallback.Count > 0)
                    {
                        fallbackList = lists.OfType<JsonObject>().FirstOrDefault(l => l["Name"]?.ToString() == TempFallbackList) ?? new JsonObject
                        {
                            ["PrefferedLocations"] = new JsonObject(), ["Name"] = TempFallbackList, ["Description"] = "Created by an XIV MCP job; removed when done.",
                            ["FolderPath"] = "MCP", ["Order"] = lists.Count, ["RemoveCompletedItems"] = false,
                        };
                        if (!lists.Contains(fallbackList)) lists.Add(fallbackList);
                        fallbackList["Fallback"] = true;
                        fallbackList["ItemIds"] = new JsonArray(fallback.Keys.Select(k => (JsonNode)JsonValue.Create(k)).ToArray());
                        fallbackList["Quantities"] = new JsonObject(fallback.Select(t => new KeyValuePair<string, JsonNode?>(t.Key.ToString(), JsonValue.Create(t.Value))));
                        fallbackList["EnabledItems"] = new JsonObject(fallback.Select(t => new KeyValuePair<string, JsonNode?>(t.Key.ToString(), JsonValue.Create(true))));
                    }
                    foreach (var other in lists.OfType<JsonObject>().Where(l => l != list && l != fallbackList && l["Enabled"]?.GetValue<bool>() == true))
                    {
                        restore.Add(other["Name"]?.ToString() ?? "");
                        other["Enabled"] = false;
                    }
                    list["Enabled"] = true;
                    if (fallbackList is not null) fallbackList["Enabled"] = true;
                    return root;
                }).ConfigureAwait(false);
                if (targets.Count == 0) throw new ToolException("Nothing to gather (the list has no enabled items).");

                var started = DateTime.UtcNow;
                try
                {
                    await WaitForPlugin(CraftGatherTools.GatherBuddy, ct).ConfigureAwait(false);
                    await Game.Run(() => { Svc.PluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled").InvokeAction(true); return true; }).ConfigureAwait(false);
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    var goal = until ?? targets;
                    var restarts = 0;
                    string? lastProgress = null;
                    var stall = new GatherStallWatch(stallWindow, DateTime.UtcNow);
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var state = await Game.Run(() =>
                        {
                            var status = CraftGatherTools.SafeString("GatherBuddyReborn.GetAutoGatherStatusText");
                            return (
                                Have: goal.ToDictionary(t => t.Key, t => Items.CountInBags(t.Key)),
                                Running: CraftGatherTools.Ipc("GatherBuddyReborn.IsAutoGatherEnabled") == true,
                                Status: status,
                                Free: Items.FreeBagSlots(),
                                Activity: GatherActivityNow(GatherStallWatch.IsWaiting(CraftGatherTools.Ipc("GatherBuddyReborn.IsAutoGatherWaiting"), status)));
                        }).ConfigureAwait(false);
                        var missing = goal.Where(t => state.Have[t.Key] < t.Value).ToList();
                        var progress = string.Join(" · ", goal.Select(t => $"{Items.Name(t.Key)} {state.Have[t.Key]}/{t.Value}"));
                        if (progress != lastProgress)
                        {
                            JobManager.Instance?.StepProgress("gather_until", progress);
                            lastProgress = progress;
                        }
                        if (missing.Count == 0)
                            return new { done = true, gathered = Describe(goal, state.Have), restarts, minutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1) };
                        if (state.Free == 0) throw new ToolException($"Bags are full; still missing {Missing(missing, state.Have)}.");
                        if (session is { } s && DateTime.UtcNow - started > s)
                            return new { done = false, session = "over", gathered = Describe(goal, state.Have), restarts, minutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1) };
                        if (DateTime.UtcNow - started > timeout) throw new ToolException($"Timed out; still missing {Missing(missing, state.Have)}.");
                        if (!state.Running)
                        {
                            if (restarts >= maxRestarts)
                                throw new ToolException($"GatherBuddy stopped ({state.Status ?? "no status"}) after {restarts} restart(s); still missing {Missing(missing, state.Have)}.");
                            restarts++;
                            Svc.Log.Information($"[MCP] gather_until: GatherBuddy stopped ({state.Status}); starting it again ({restarts}/{maxRestarts}).");
                            await Task.Delay(30000, ct).ConfigureAwait(false);
                            await Game.Run(() => { Svc.PluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled").InvokeAction(true); return true; }).ConfigureAwait(false);
                            await Task.Delay(5000, ct).ConfigureAwait(false);
                            stall.Reset(DateTime.UtcNow);
                            continue;
                        }
                        if (stall.Observe(DateTime.UtcNow, state.Have, state.Activity))
                        {
                            Svc.Log.Warning($"[MCP] gather_until: GatherBuddy idle for {stall.IdleFor(DateTime.UtcNow).TotalMinutes:0} min ({state.Status}); giving up.");
                            throw new ToolException(GatherStallWatch.Message(missing.Select(m => $"{m.Value - state.Have[m.Key]}x {Items.Name(m.Key)}"),
                                state.Status, stall.IdleFor(DateTime.UtcNow)));
                        }
                        await Task.Delay(10000, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await Game.Run(() => { try { Svc.PluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled").InvokeAction(false); } catch { } return true; }).ConfigureAwait(false);
                    // Put the player's lists back as they were (and drop the temporary one).
                    try
                    {
                        await Task.Delay(1500, CancellationToken.None).ConfigureAwait(false);
                        await PluginTools.ModifyPluginJson(CraftGatherTools.GatherBuddy, CraftGatherTools.GatherListsFile, root =>
                        {
                            var lists = (JsonArray)root;
                            foreach (var l in lists.OfType<JsonObject>())
                            {
                                if (restore.Contains(l["Name"]?.ToString() ?? "")) l["Enabled"] = true;
                                else if (l["Name"]?.ToString() == usedList && listArg is not null) l["Enabled"] = false;
                            }
                            if (listArg is null && lists.OfType<JsonObject>().FirstOrDefault(l => l["Name"]?.ToString() == TempGatherList) is { } temp) lists.Remove(temp);
                            if (lists.OfType<JsonObject>().FirstOrDefault(l => l["Name"]?.ToString() == TempFallbackList) is { } tempFallback) lists.Remove(tempFallback);
                            return root;
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex) { Svc.Log.Warning($"[MCP] Could not restore GatherBuddy lists: {ex.Message}"); }
                }
            },
        };

        yield return new McpTool
        {
            Name = "run_crafting_list",
            Description = "Runs an Artisan crafting list (id or name, e.g. from prepare_craft_plan) to the end and reports what was crafted (items " +
                          "and crafts made per recipe, counted as the products arrive, so intermediates count even when later crafts use them up). " +
                          "Fails (job: pending) if Artisan stops without crafting anything (e.g. missing materials), and when Artisan stays busy " +
                          "without any craft progress for 'stall_minutes' (default 5): it is then stopped and the error names the recipe it is " +
                          "stuck on — typically one with an equipment ingredient, which Artisan can't select in the crafting log. " +
                          "Cancelling stops Artisan. Meant as a job step (start_job): when the same step runs again (resume after a pause, " +
                          "retry after a failure) it continues instead of starting over — the Artisan list is shrunk to the crafts still missing, " +
                          "and gets its original quantities back once everything is made. Requires Artisan and 'Crafting & gathering' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "Artisan list id or name." },
                    "timeout_minutes": { "type": "integer", "description": "Give up after this long (default 600)." },
                    "stall_minutes": { "type": "integer", "description": "Stop and fail when Artisan is busy but nothing is crafted (no bag count changes) for this long (default 5; 0 = never)." }
                  },
                  "required": ["list"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var listArg = args.String("list") ?? throw new ToolException("'list' is required.");
                var timeout = TimeSpan.FromMinutes(args.Int("timeout_minutes", 600, 1, 24 * 60));
                var stallAfter = TimeSpan.FromMinutes(args.Int("stall_minutes", 5, 0, 120));
                var (listId, listName, current) = await Game.RunLoggedIn(() =>
                {
                    CraftGatherTools.RequireArtisan();
                    CraftGatherTools.EnsureArtisanIdle();
                    var lists = PluginTools.ReadPluginJson(CraftGatherTools.Artisan, null)["NewCraftingLists"] as JsonArray ?? [];
                    var list = CraftGatherTools.MatchList(lists, listArg) ?? throw new ToolException($"No Artisan list '{listArg}'.");
                    var counts = (list["Recipes"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(r => new RecipeCount(ItemSourceTools.UInt(r["ID"]), JsonNumber.Int(r["Quantity"]) ?? 0))
                        .Where(r => r.RecipeId != 0 && r.Crafts > 0).ToList();
                    if (Svc.PluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus").InvokeFunc())
                        throw new ToolException("Artisan's Endurance mode is on; lists can't start while it is active.");
                    return (list["ID"]!.GetValue<int>(), list["Name"]?.ToString() ?? listArg, counts);
                }).ConfigureAwait(false);

                // Run again after a pause or failure (job resume / retry): Artisan would start the list from the top and craft
                // everything again, so the list is shrunk to what the earlier runs haven't made.
                const string tool = "run_crafting_list";
                var resume = CraftListResume.Plan(CraftListMemory.FromJson(JobManager.Instance?.StepMemory(tool)), listId, current);
                var written = current;
                string? resumeNote = null;
                if (resume.Shrink is { } left && !resume.AllDone)
                {
                    await CraftGatherTools.WriteCraftingList(listId.ToString(), null, left.Select(r => (r.RecipeId, r.Crafts)).ToList(), append: false).ConfigureAwait(false);
                    await WaitForPlugin(CraftGatherTools.Artisan, ct).ConfigureAwait(false);
                    written = left;
                    resumeNote = $"Continued an earlier run: the list was shrunk to the {left.Sum(r => r.Crafts)} craft(s) still missing.";
                }

                var plan = await Game.RunLoggedIn(() =>
                {
                    CraftGatherTools.RequireArtisan();
                    var recipes = Svc.Data.GetExcelSheet<Recipe>();
                    var entries = new List<ListRecipe>();
                    var watched = new HashSet<uint>();
                    foreach (var r in resume.Original)
                    {
                        if (recipes.GetRowOrDefault(r.RecipeId) is not { } rec || rec.ItemResult.RowId == 0) continue;
                        entries.Add(new ListRecipe(rec.RowId, Items.Name(rec.ItemResult.RowId), rec.ItemResult.RowId, r.Crafts, Math.Max(1, (int)rec.AmountResult),
                                                   CraftPlanner.GearIngredientNames(rec)));
                        watched.Add(rec.ItemResult.RowId);
                        foreach (var (ing, _) in CraftPlanner.Ingredients(rec)) watched.Add(ing);
                    }
                    if (entries.Count == 0) throw new ToolException($"The Artisan list '{listName}' has no recipes.");
                    return (Id: listId, Name: listName, Entries: entries, Watched: watched.ToList(), Before: watched.ToDictionary(k => k, Items.CountInBags));
                }).ConfigureAwait(false);

                var started = DateTime.UtcNow;
                var progress = new CraftListProgress(plan.Entries, plan.Before, started, resume.Done);
                Dictionary<uint, int> Counts() => plan.Watched.ToDictionary(k => k, Items.CountInBags);
                var saved = progress.Done();
                void Remember(bool force = false)
                {
                    var done = progress.Done();
                    if (!force && done.SequenceEqual(saved)) return;
                    saved = done;
                    JobManager.Instance?.SetStepMemory(tool, new CraftListMemory(plan.Id, resume.Original, written, done).ToJson());
                }
                async Task Settle()
                {
                    // A craft that was in progress when Artisan stopped may still deliver its product.
                    await Task.Delay(3000, CancellationToken.None).ConfigureAwait(false);
                    progress.Observe(await Game.Run(Counts).ConfigureAwait(false), DateTime.UtcNow);
                    Remember();
                }
                Remember(force: true);

                if (resume.AllDone)
                    return new { list = plan.Name, minutes = 0.0, crafted = Report(plan.Entries, progress), resumed = (string?)null,
                                 restored = written.SequenceEqual(resume.Original) ? null : await RestoreList(plan.Id, resume.Original).ConfigureAwait(false),
                                 note = (string?)"Every craft of this list was already made by an earlier run of this step." };

                await Game.Run(() => { Svc.PluginInterface.GetIpcSubscriber<int, object>("Artisan.StartListById").InvokeAction(plan.Id); return true; }).ConfigureAwait(false);
                try
                {
                    var sawRunning = false;
                    for (var tick = 1; ; tick++)
                    {
                        // Counts every second, so a product made just before the next craft uses it up is still seen.
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                        var counts = await Game.Run(Counts).ConfigureAwait(false);
                        progress.Observe(counts, DateTime.UtcNow);
                        Remember();
                        if (tick % 5 != 0) continue;
                        var (running, paused, solving) = await Game.Run(() => (
                            CraftGatherTools.Ipc("Artisan.IsListRunning") == true || CraftGatherTools.Ipc("Artisan.IsBusy") == true,
                            CraftGatherTools.Ipc("Artisan.IsListPaused") == true,
                            RaphaelBusy())).ConfigureAwait(false);
                        var now = DateTime.UtcNow;
                        if (running) { sawRunning = true; }
                        else if (sawRunning || now - started > TimeSpan.FromSeconds(30)) break;
                        // A paused list or a solver still working isn't stuck.
                        if (paused || solving) progress.Touch(now);
                        if (running && progress.Stalled(now, stallAfter))
                        {
                            var idle = progress.Idle(now);
                            await Game.Run(() => { CraftGatherTools.StopArtisan(); return true; }).ConfigureAwait(false);
                            await Settle().ConfigureAwait(false);
                            throw new ToolException(progress.StallMessage(plan.Name, idle) + Continues(progress));
                        }
                        if (now - started > timeout) throw new ToolException("Timed out while Artisan was still crafting." + Continues(progress));
                    }
                }
                catch (OperationCanceledException)
                {
                    await Game.Run(() => { CraftGatherTools.StopArtisan(); return true; }).ConfigureAwait(false);
                    await Settle().ConfigureAwait(false);
                    throw;
                }

                progress.Observe(await Game.Run(Counts).ConfigureAwait(false), DateTime.UtcNow);
                Remember();
                if (!progress.MadeProgressThisRun && !progress.Finished)
                    throw new ToolException($"Artisan stopped without crafting anything from '{plan.Name}' (stopped at {progress.Current!.Item}: missing materials, wrong job or gear?)." +
                                            (GearIngredients.Note(progress.Current.GearIngredients) is { } manual ? " " + manual : ""));
                var restored = progress.Finished && !written.SequenceEqual(resume.Original) ? await RestoreList(plan.Id, resume.Original).ConfigureAwait(false) : null;
                return new { list = plan.Name, minutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1), crafted = Report(plan.Entries, progress),
                             resumed = resumeNote, restored,
                             note = progress.Finished ? null : $"The list stopped early, at {progress.Current!.Item}: not every craft was made." + Continues(progress) };
            },
        };
    }

    /// <summary>
    /// What the character is doing for the stall check: gathering, fishing/spearfishing, mounted or flying, walking a path,
    /// teleporting or loading a zone counts as busy. Framework thread.
    /// </summary>
    private static GatherActivity GatherActivityNow(bool waiting)
    {
        var c = Svc.Condition;
        var busy = c[ConditionFlag.Gathering] || c[ConditionFlag.ExecutingGatheringAction] || c[ConditionFlag.Fishing]
                   || c[ConditionFlag.Mounted] || c[ConditionFlag.InFlight] || c[ConditionFlag.Diving] || c[ConditionFlag.Jumping]
                   || c[ConditionFlag.Casting] || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
                   || c[ConditionFlag.OccupiedInCutSceneEvent] || Navigation.PathRunning;
        var pos = Svc.Objects.LocalPlayer?.Position ?? default;
        return new GatherActivity(busy, waiting, pos.X, pos.Y, pos.Z);
    }

    private static object Describe(Dictionary<uint, int> targets, Dictionary<uint, int> have) =>
        targets.Select(t => new { item = Items.Name(t.Key), target = t.Value, inBags = have[t.Key] }).ToList();

    private static string Missing(List<KeyValuePair<uint, int>> missing, Dictionary<uint, int> have) =>
        string.Join(", ", missing.Select(m => $"{m.Value - have[m.Key]}x {Items.Name(m.Key)}"));

    private static object Report(List<ListRecipe> entries, CraftListProgress progress) => entries.Select(e => new
    {
        item = e.Item, recipeId = e.RecipeId, crafts = e.Crafts, craftsDone = progress.CraftsDone(e.RecipeId),
        expected = e.Crafts * e.Yield, made = progress.ItemsMade(e.RecipeId),
    }).ToList();

    private static string Continues(CraftListProgress progress)
    {
        var done = progress.Done().Values.Sum();
        return done == 0 ? "" : $" {done} craft(s) are done; running this job step again (retry / resume) continues with the rest instead of crafting them again.";
    }

    /// <summary>Puts the list's original quantities back after a resumed run shrank it and everything is crafted.</summary>
    private static async Task<string?> RestoreList(int listId, List<RecipeCount> original)
    {
        try
        {
            await CraftGatherTools.WriteCraftingList(listId.ToString(), null, original.Select(r => (r.RecipeId, r.Crafts)).ToList(), append: false).ConfigureAwait(false);
            return "The Artisan list has its original quantities again.";
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[MCP] Could not restore Artisan list {listId}: {ex.Message}");
            return $"The Artisan list still holds only the crafts that were missing (restoring it failed: {ex.Message}).";
        }
    }

    /// <summary>Artisan's Raphael solver is building a solution (a craft waits for it). Framework thread.</summary>
    private static bool RaphaelBusy()
    {
        try { return ArtisanBridge.AnyInProgress(); }
        catch { return false; }
    }

    private static async Task WaitForPlugin(string name, CancellationToken ct)
    {
        for (var i = 0; i < 40 && !PluginCompat.IsLoaded(name); i++) await Task.Delay(250, ct).ConfigureAwait(false);
        await Task.Delay(1500, ct).ConfigureAwait(false);
    }
}
