using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
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
                          "Other active auto-gather lists are switched off meanwhile and restored afterwards. Fails (job: pending) if GatherBuddy " +
                          "stops on its own before the targets are reached, or the bags are full. Meant as a job step (start_job) — it can run for " +
                          "hours. Requires GatherBuddy Reborn and 'Crafting & gathering' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "items": { "type": "array", "items": { "type": "object", "properties": { "item": { "type": "string" }, "quantity": { "type": "integer" } }, "required": ["item", "quantity"] } },
                    "list": { "type": "string", "description": "Existing GatherBuddy list (name or index) instead of items." },
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
                    foreach (var other in lists.OfType<JsonObject>().Where(l => l != list && l["Enabled"]?.GetValue<bool>() == true))
                    {
                        restore.Add(other["Name"]?.ToString() ?? "");
                        other["Enabled"] = false;
                    }
                    list["Enabled"] = true;
                    return root;
                }).ConfigureAwait(false);
                if (targets.Count == 0) throw new ToolException("Nothing to gather (the list has no enabled items).");

                var started = DateTime.UtcNow;
                try
                {
                    await WaitForPlugin(CraftGatherTools.GatherBuddy, ct).ConfigureAwait(false);
                    await Game.Run(() => { Svc.PluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled").InvokeAction(true); return true; }).ConfigureAwait(false);
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var state = await Game.Run(() => (
                            Have: targets.ToDictionary(t => t.Key, t => Items.CountInBags(t.Key)),
                            Running: CraftGatherTools.Ipc("GatherBuddyReborn.IsAutoGatherEnabled") == true,
                            Status: CraftGatherTools.SafeString("GatherBuddyReborn.GetAutoGatherStatusText"),
                            Free: Items.FreeBagSlots())).ConfigureAwait(false);
                        var missing = targets.Where(t => state.Have[t.Key] < t.Value).ToList();
                        if (missing.Count == 0)
                            return new { done = true, gathered = Describe(targets, state.Have), minutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1) };
                        if (state.Free == 0) throw new ToolException($"Bags are full; still missing {Missing(missing, state.Have)}.");
                        if (!state.Running) throw new ToolException($"GatherBuddy stopped ({state.Status ?? "no status"}); still missing {Missing(missing, state.Have)}.");
                        if (DateTime.UtcNow - started > timeout) throw new ToolException($"Timed out; still missing {Missing(missing, state.Have)}.");
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
                          "added to the bags per recipe). Fails (job: pending) if Artisan stops before the list is done (e.g. missing materials). " +
                          "Cancelling stops Artisan. Meant as a job step (start_job). Requires Artisan and 'Crafting & gathering' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "Artisan list id or name." },
                    "timeout_minutes": { "type": "integer", "description": "Give up after this long (default 600)." }
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
                var plan = await Game.RunLoggedIn(() =>
                {
                    CraftGatherTools.RequireArtisan();
                    CraftGatherTools.EnsureArtisanIdle();
                    var lists = PluginTools.ReadPluginJson(CraftGatherTools.Artisan, null)["NewCraftingLists"] as JsonArray ?? [];
                    var list = CraftGatherTools.MatchList(lists, listArg) ?? throw new ToolException($"No Artisan list '{listArg}'.");
                    var recipes = Svc.Data.GetExcelSheet<Recipe>();
                    var expected = (list["Recipes"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(r => recipes.GetRowOrDefault(r["ID"]?.GetValue<uint>() ?? 0) is { } rec
                            ? (Item: rec.ItemResult.RowId, Count: (r["Quantity"]?.GetValue<int>() ?? 0) * Math.Max(1, (int)rec.AmountResult)) : (Item: 0u, Count: 0))
                        .Where(x => x.Item != 0).GroupBy(x => x.Item).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
                    if (Svc.PluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus").InvokeFunc())
                        throw new ToolException("Artisan's Endurance mode is on; lists can't start while it is active.");
                    return (Id: list["ID"]!.GetValue<int>(), Name: list["Name"]?.ToString(), Expected: expected, Before: expected.Keys.ToDictionary(k => k, Items.CountInBags));
                }).ConfigureAwait(false);

                var started = DateTime.UtcNow;
                await Game.Run(() => { Svc.PluginInterface.GetIpcSubscriber<int, object>("Artisan.StartListById").InvokeAction(plan.Id); return true; }).ConfigureAwait(false);
                try
                {
                    var sawRunning = false;
                    while (true)
                    {
                        await Task.Delay(5000, ct).ConfigureAwait(false);
                        var running = await Game.Run(() => CraftGatherTools.Ipc("Artisan.IsListRunning") == true || CraftGatherTools.Ipc("Artisan.IsBusy") == true).ConfigureAwait(false);
                        if (running) { sawRunning = true; }
                        else if (sawRunning || DateTime.UtcNow - started > TimeSpan.FromSeconds(30)) break;
                        if (DateTime.UtcNow - started > timeout) throw new ToolException("Timed out while Artisan was still crafting.");
                    }
                }
                catch (OperationCanceledException)
                {
                    await Game.Run(() => { try { Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest").InvokeAction(true); } catch { } return true; }).ConfigureAwait(false);
                    throw;
                }

                var after = await Game.Run(() => plan.Expected.Keys.ToDictionary(k => k, Items.CountInBags)).ConfigureAwait(false);
                var made = plan.Expected.ToDictionary(e => e.Key, e => after[e.Key] - plan.Before[e.Key]);
                var shortItems = plan.Expected.Where(e => made[e.Key] < e.Value).ToList();
                var report = plan.Expected.Select(e => new { item = Items.Name(e.Key), expected = e.Value, made = made[e.Key] }).ToList();
                // Intermediates get used up by later crafts, so only the list's final products are a fair check: items nothing else consumes.
                if (shortItems.Count > 0 && shortItems.All(s => made[s.Key] <= 0) && report.Count > 0 && report.All(r => r.made <= 0))
                    throw new ToolException($"Artisan stopped without crafting anything from '{plan.Name}' (missing materials, wrong job or gear?).");
                return new { list = plan.Name, minutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1), crafted = report,
                             note = shortItems.Count > 0 ? "Some outputs are below the expected count — intermediates are consumed by later crafts, or the list stopped early." : null };
            },
        };
    }

    private static object Describe(Dictionary<uint, int> targets, Dictionary<uint, int> have) =>
        targets.Select(t => new { item = Items.Name(t.Key), target = t.Value, inBags = have[t.Key] }).ToList();

    private static string Missing(List<KeyValuePair<uint, int>> missing, Dictionary<uint, int> have) =>
        string.Join(", ", missing.Select(m => $"{m.Value - have[m.Key]}x {Items.Name(m.Key)}"));

    private static async Task WaitForPlugin(string name, CancellationToken ct)
    {
        for (var i = 0; i < 40 && !PluginCompat.IsLoaded(name); i++) await Task.Delay(250, ct).ConfigureAwait(false);
        await Task.Delay(1500, ct).ConfigureAwait(false);
    }
}
