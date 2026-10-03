using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Planning bigger crafting projects and preparing them in Artisan (ordered list + pre-built Raphael solutions).</summary>
internal static class CraftPlanTools
{
    private const string ItemsSchema = """
        "items": {
          "type": "array",
          "description": "What to craft: [{ \"item\": name or id, \"quantity\": number wanted, or \"fill\" = as many as fit in the bags (and, for collectables, under the scrip cap) }].",
          "items": { "type": "object", "properties": { "item": { "type": "string" }, "quantity": { "type": ["integer", "string"] } }, "required": ["item"] }
        },
        "craft_intermediates": { "type": "boolean", "description": "Craft intermediate materials yourself instead of obtaining them (default true)." },
        "use_retainers": { "type": "boolean", "description": "Count materials on your retainers (cached inventories, default true)." },
        "count_existing": { "type": "boolean", "description": "Subtract finished items you already have from the wanted quantity (default false: craft the full quantity)." },
        "respect_scrip_cap": { "type": "boolean", "description": "For quantity \"fill\" of collectables: no more than can be turned in before the scrip cap (default true)." }
        """;

    public static IEnumerable<McpTool> Create(Configuration config, RetainerTracker retainers)
    {
        yield return new McpTool
        {
            Name = "plan_craft",
            Description = "Plans a crafting project without changing anything: resolves the full recipe tree, picks recipes your jobs can craft, " +
                          "orders the crafts (intermediates first), nets materials against your bags and cached retainer inventories, and lists what " +
                          "is still missing with where to get it (gathering via GatherBuddy, gil vendors, retainer ventures, and — with 'Online " +
                          "lookups' — exchanges, drops and more). It simulates bag space through the whole project and splits it into batches when it " +
                          "would not fit. With Artisan installed it also shows, per recipe, the stats Artisan will craft with (gearset + food + potion) " +
                          "and whether a Raphael solution is already cached for exactly those stats. Then use prepare_craft_plan.",
            InputSchema = $$"""{ "type": "object", "properties": { {{ItemsSchema}} }, "required": ["items"] }""",
            Handler = async (args, ct) =>
            {
                var (targets, options) = await ParseArgs(args, retainers).ConfigureAwait(false);
                var plan = await Game.RunLoggedIn(() => CraftPlanner.Build(targets, options, retainers)).ConfigureAwait(false);
                var sources = await MaterialSources(plan.Materials.Where(m => m.Missing > 0).Select(m => m.ItemId).ToList(), config, ct).ConfigureAwait(false);
                return await Game.Run(() => Describe(plan, sources, raphael: CraftGatherTools.ArtisanLoaded)).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "prepare_craft_plan",
            Description = "Prepares a planned project (same arguments as plan_craft, plus the batch to prepare) in Artisan: creates an Artisan crafting " +
                          "list with the crafts in the right order, switches those recipes to Artisan's Raphael solver and builds the Raphael solution " +
                          "for each recipe ahead of time — with exactly the stats Artisan will craft with (the job's gearset + the recipe's food and " +
                          "potion settings), so the craft starts with a cached optimal macro instead of solving (and possibly timing out) mid-list. " +
                          "Solutions already cached are reused; calling again continues unfinished ones. Missing materials are reported, not obtained — " +
                          "get them first (buy_item, gathering lists, assign_venture, retainer withdrawals), then crafting_control start_list. Requires " +
                          "Artisan and 'Crafting & gathering'.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    {{ItemsSchema}},
                    "batch": { "type": "integer", "description": "Which batch to prepare when the plan is split for bag space (default 1)." },
                    "list_name": { "type": "string", "description": "Name of the Artisan list (default \"MCP: <first item>\"). An existing list with this name is replaced." },
                    "raphael": { "type": "boolean", "description": "Build Raphael solutions (default true)." },
                    "timeout_seconds": { "type": "integer", "description": "How long to wait for Raphael in total (default 240); unfinished ones keep building in Artisan." }
                  },
                  "required": ["items"]
                }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = async (args, ct) =>
            {
                if (!config.AllowCraftingGathering)
                    throw new ToolException("Crafting & gathering automation is disabled. Enable it in the XIV MCP settings window (/xivmcp) in game.");
                var (targets, options) = await ParseArgs(args, retainers).ConfigureAwait(false);
                var batchIndex = args.Int("batch", 1, 1, 50);
                var useRaphael = args.Bool("raphael", true);
                var deadline = DateTime.UtcNow.AddSeconds(args.Int("timeout_seconds", 240, 10, 900));

                var (plan, totalBatches) = await Game.RunLoggedIn(() =>
                {
                    CraftGatherTools.RequireArtisan();
                    CraftGatherTools.EnsureArtisanIdle();
                    var full = CraftPlanner.Build(targets, options, retainers);
                    var batch = full.Batches.FirstOrDefault(b => b.Index == batchIndex)
                                ?? throw new ToolException($"The plan has {full.Batches.Count} batch(es); there is no batch {batchIndex}.");
                    return (Plan: full.Batches.Count == 1 ? full : CraftPlanner.Build(batch.Targets, options, retainers), Total: full.Batches.Count);
                }).ConfigureAwait(false);
                var blocking = plan.Steps.Where(s => s.Problem is not null).ToList();
                if (blocking.Count > 0)
                    throw new ToolException("Some crafts can't be done yet: " + string.Join("; ", blocking.Select(s => $"{s.Item}: {s.Problem}")));
                if (plan.Steps.Count == 0) throw new ToolException("Nothing to craft (everything is already in stock).");

                // 1. The Artisan list (this reloads Artisan, so it comes before the in-memory Raphael work).
                var name = args.String("list_name") ?? $"MCP: {Items.Name(targets[0].ItemId)}" + (totalBatches > 1 ? $" (batch {batchIndex})" : "");
                var existing = await Game.Run(() => ExistingList(name)).ConfigureAwait(false);
                var (listId, backup) = await CraftGatherTools.WriteCraftingList(existing, name, plan.Steps.Select(s => (s.RecipeId, s.Crafts)).ToList(), append: false).ConfigureAwait(false);

                // 2. Raphael: select it per recipe and build the solutions, one at a time.
                var raphael = new List<object>();
                var warnings = new List<string>(plan.Warnings);
                if (useRaphael)
                {
                    await WaitForArtisan(ct).ConfigureAwait(false);
                    if (!await Game.Run(ArtisanBridge.CliAvailable).ConfigureAwait(false))
                        warnings.Add("Artisan's Raphael solver (raphael-cli) is missing — check that your anti-virus didn't remove it; recipes keep their current solver.");
                    else
                    {
                        foreach (var job in plan.Steps.Select(s => s.Job).Distinct())
                            if (!await Game.Run(() => HasGearset(job)).ConfigureAwait(false))
                                warnings.Add($"No gearset for {job}: Artisan can't switch to it, and the predicted stats (and Raphael solutions) use your current gear.");

                        var changed = false;
                        foreach (var step in plan.Steps)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (step.JobLevelNeeded > 0 && step.PlayerJobLevel < 7)
                            {
                                raphael.Add(new { item = step.Item, skipped = "Raphael needs crafter level 7 (Master's Mend)." });
                                continue;
                            }
                            changed |= await Game.Run(() => ArtisanBridge.SelectRaphael(step.RecipeId)).ConfigureAwait(false);
                            var before = await Game.Run(() => ArtisanBridge.Predict(step.RecipeId)).ConfigureAwait(false);
                            if (!before.HasSolution && !before.InProgress && DateTime.UtcNow < deadline)
                                await Game.Run(() => { ArtisanBridge.Build(step.RecipeId); return true; }).ConfigureAwait(false);
                            var started = DateTime.UtcNow;
                            var now = before;
                            while (!now.HasSolution && DateTime.UtcNow < deadline)
                            {
                                await Task.Delay(500, ct).ConfigureAwait(false);
                                now = await Game.Run(() => ArtisanBridge.Predict(step.RecipeId)).ConfigureAwait(false);
                                if (!now.InProgress && !now.HasSolution) break; // finished without a result (failed or timed out in Artisan)
                            }
                            raphael.Add(new
                            {
                                item = step.Item,
                                recipeId = step.RecipeId,
                                solution = now.HasSolution ? (before.HasSolution ? "already cached" : $"built in {(DateTime.UtcNow - started).TotalSeconds:0.#} s") : now.InProgress ? "still building" : "failed (Artisan will fall back to its standard solver)",
                                macroSteps = now.Steps,
                                stats = new { craftsmanship = now.Craftsmanship, control = now.Control, cp = now.Cp, level = now.Level },
                                food = now.Food == 0 ? null : Items.Name(now.Food),
                                potion = now.Potion == 0 ? null : Items.Name(now.Potion),
                            });
                        }
                        await Game.Run(() => { ArtisanBridge.Save(); return true; }).ConfigureAwait(false);
                        if (changed) warnings.Add("Switched the list's recipes to Artisan's Raphael solver (per-recipe setting in Artisan).");
                    }
                }

                var missing = plan.Materials.Where(m => m.Missing > 0).Select(m => new { item = m.Item, missing = m.Missing }).ToList();
                var withdraw = plan.Materials.Where(m => m.OnRetainers > 0)
                    .SelectMany(m => m.RetainerStock.Select(r => new { item = m.Item, retainer = r.Retainer, quantity = r.Quantity })).ToList();
                return new
                {
                    list = new { id = listId, name, crafts = plan.Steps.Select(s => $"{s.Crafts}x {s.Item} ({s.Job})").ToList() },
                    batch = new { index = batchIndex, of = totalBatches, extraBagSlots = plan.PeakSlotsNeeded, freeBagSlots = plan.FreeSlots },
                    raphael,
                    raphaelSettings = useRaphael ? await Game.Run(ArtisanBridge.Settings).ConfigureAwait(false) : null,
                    withdrawFromRetainers = withdraw.Count > 0 ? withdraw : null,
                    stillMissing = missing.Count > 0 ? missing : null,
                    warnings,
                    backup,
                    next = missing.Count > 0 || withdraw.Count > 0
                        ? "Get the missing materials into your bags first (see plan_craft for sources), then crafting_control action=start_list list=" + listId
                        : "Ready: crafting_control action=start_list list=" + listId,
                };
            },
        };
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<(List<CraftPlanner.Target>, CraftPlanner.Options)> ParseArgs(ToolArgs args, RetainerTracker retainers)
    {
        var raw = (args.Node("items") as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (raw.Count == 0) throw new ToolException("'items' needs at least one { item, quantity }.");
        var options = new CraftPlanner.Options(args.Bool("craft_intermediates", true), args.Bool("use_retainers", true), args.Bool("count_existing", false));
        var respectCap = args.Bool("respect_scrip_cap", true);
        var targets = await Game.Run(() =>
        {
            var parsed = raw.Select(r =>
            {
                var a = new ToolArgs(r);
                var item = Items.Resolve(a.String("item") ?? throw new ToolException("Each entry needs 'item'."));
                var fill = string.Equals(a.String("quantity"), "fill", StringComparison.OrdinalIgnoreCase);
                return (Item: item.RowId, Quantity: fill ? 0 : a.Int("quantity", 1, 1, 99999), Fill: fill);
            }).ToList();
            var fills = parsed.Where(p => p.Fill).Select(p => p.Item).Distinct().ToList();
            if (fills.Count > 1) throw new ToolException("Only one item can use quantity \"fill\".");
            var fixedTargets = parsed.Where(p => !p.Fill).GroupBy(p => p.Item).Select(g => new CraftPlanner.Target(g.Key, g.Sum(p => p.Quantity))).ToList();
            if (fills.Count == 0) return fixedTargets;
            var n = FillQuantity(fills[0], fixedTargets, options, retainers, respectCap);
            if (n <= 0) throw new ToolException($"No {Items.Name(fills[0])} fits: no free bag space" + (respectCap ? " or no room under the scrip cap." : "."));
            return [.. fixedTargets, new CraftPlanner.Target(fills[0], n)];
        }).ConfigureAwait(false);
        return (targets, options);
    }

    /// <summary>
    /// The largest quantity of an item whose whole project (materials coming in, crafts going out) fits into the free bag slots —
    /// and, for collectables, that can still be turned in before its scrip's cap (at the top collectability tier). Framework thread.
    /// </summary>
    private static int FillQuantity(uint itemId, List<CraftPlanner.Target> others, CraftPlanner.Options options, RetainerTracker? retainers, bool respectCap)
    {
        var free = Items.FreeBagSlots();
        var upper = Math.Max(1, free * 99);
        if (respectCap && Collectables.RewardFor(itemId) is { HighReward: > 0 } reward)
        {
            var room = Collectables.CurrencyRoom(reward.CurrencyItemId).Room;
            if (room != long.MaxValue) upper = (int)Math.Min(upper, room / reward.HighReward);
        }
        bool Fits(int n) => CraftPlanner.Build([.. others, new CraftPlanner.Target(itemId, n)], options with { FreeSlotsOverride = int.MaxValue }, retainers).PeakSlotsNeeded <= free;
        int lo = 0, hi = upper;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (Fits(mid)) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private static object Describe(CraftPlanner.Plan plan, Dictionary<uint, object> sources, bool raphael) => new
    {
        order = plan.Steps.Select((s, i) => new
        {
            step = i + 1,
            craft = s.Item,
            recipeId = s.RecipeId,
            job = s.Job,
            level = s.JobLevelNeeded,
            crafts = s.Crafts,
            produces = s.Produces,
            uses = s.Ingredients.Select(x => $"{x.Amount}x {Items.Name(x.ItemId)}").ToList(),
            problem = s.Problem,
            raphael = raphael ? RaphaelInfo(s) : null,
            collectable = s.IsTarget && Collectables.RewardFor(s.ItemId) is { } reward
                ? new { turnIn = Collectables.Describe(reward), scripForAll = (long)reward.HighReward * s.Produces, note = "Raphael solutions for collectables aim for the highest collectability tier." }
                : null,
        }).ToList(),
        materials = plan.Materials.Select(m => new
        {
            item = m.Item,
            needed = m.Needed,
            inBags = m.InBags,
            fromRetainers = m.RetainerStock.Count > 0 ? m.RetainerStock.Select(r => new { retainer = r.Retainer, quantity = r.Quantity }).ToList() : null,
            missing = m.Missing,
            crystal = m.Crystal ? true : (bool?)null,
            note = m.Note,
            howToGet = m.Missing > 0 && sources.TryGetValue(m.ItemId, out var s) ? s : null,
        }).ToList(),
        inventory = new
        {
            freeBagSlots = plan.FreeSlots,
            extraSlotsAtPeak = plan.PeakSlotsNeeded,
            fits = plan.Batches.Count == 1,
            batches = plan.Batches.Count > 1
                ? plan.Batches.Select(b => new { batch = b.Index, craft = b.Targets.Select(t => $"{t.Quantity}x {Items.Name(t.ItemId)}").ToList(), extraSlotsAtPeak = b.PeakSlotsNeeded }).ToList()
                : null,
            note = "Crystals and currencies need no bag slots; materials are assumed to stack with what is already in the bags.",
        },
        retainerDataFrom = plan.RetainerDataFrom,
        warnings = plan.Warnings,
    };

    private static object? RaphaelInfo(CraftPlanner.Step s)
    {
        try
        {
            var p = ArtisanBridge.Predict(s.RecipeId);
            return new
            {
                solverSelected = p.RaphaelSelected,
                solutionCached = p.HasSolution,
                building = p.InProgress ? true : (bool?)null,
                stats = new { craftsmanship = p.Craftsmanship, control = p.Control, cp = p.Cp, level = p.Level },
                food = p.Food == 0 ? null : Items.Name(p.Food),
                potion = p.Potion == 0 ? null : Items.Name(p.Potion),
            };
        }
        catch (Exception ex)
        {
            return new { unavailable = ex is ToolException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    /// <summary>Short "how to get it" per missing material: gathering, gil vendors, ventures (game data) plus Teamcraft kinds when online.</summary>
    private static async Task<Dictionary<uint, object>> MaterialSources(List<uint> items, Configuration config, CancellationToken ct)
    {
        var teamcraft = new Dictionary<uint, JsonArray?>();
        if (config.AllowOnlineData)
            foreach (var id in items)
                teamcraft[id] = await Teamcraft.Sources(id, ct).ConfigureAwait(false);
        return await Game.Run(() => items.ToDictionary(id => id, id =>
        {
            var options = new List<object>();
            if (Gatherable.Value.TryGetValue(id, out var g))
                options.Add(new { how = "gather", job = g.Job, level = g.Level, tool = CraftGatherTools.GatherBuddyLoaded ? "set_gather_list + set_auto_gather" : null });
            if (Fish.Value.Contains(id))
                options.Add(new { how = "fish", job = "FSH", tool = CraftGatherTools.GatherBuddyLoaded ? "set_gather_list + set_auto_gather" : null });
            switch (ShopTools.SoldByVendor(id))
            {
                case true: options.Add(new { how = "buy", from = "NPC vendor", tool = "find_vendors / buy_item" }); break;
                case null: options.Add(new { how = "buy?", hint = "install Item Vendor Location to see vendors (/xivmcp → Permissions → Market & purchases)" }); break;
            }
            if (Ventured.Value.Contains(id))
                options.Add(new { how = "venture", tool = "find_ventures / assign_venture" });
            if (teamcraft.TryGetValue(id, out var tc) && tc is not null)
            {
                var kinds = ItemSourceTools.Describe(tc, [], 2);
                foreach (var kind in new[] { "exchange", "drop", "desynthesis", "reduction", "duty", "treasure", "fate", "voyage" })
                    if (kinds.TryGetValue(kind, out var detail)) options.Add(new { how = kind, detail });
            }
            return (object)(options.Count > 0 ? options : new List<object> { new { how = "unknown", hint = config.AllowOnlineData ? "see get_item_sources" : "enable 'Online lookups' for get_item_sources" } });
        })).ConfigureAwait(false);
    }

    private static readonly Lazy<Dictionary<uint, (string Job, int Level)>> Gatherable = new(() =>
    {
        var gatheringItems = Svc.Data.GetExcelSheet<GatheringItem>();
        var result = new Dictionary<uint, (string, int)>();
        foreach (var point in Svc.Data.GetExcelSheet<GatheringPointBase>())
        {
            var job = point.GatheringType.RowId switch { 0 or 1 => "MIN", 2 or 3 => "BTN", 4 or 5 => "FSH", _ => "?" };
            foreach (var gi in point.Item)
            {
                if (gi.RowId == 0 || gatheringItems.GetRowOrDefault(gi.RowId) is not { } row || row.Item.RowId == 0) continue;
                var level = row.GatheringItemLevel.ValueNullable?.GatheringItemLevel ?? point.GatheringLevel;
                if (!result.TryGetValue(row.Item.RowId, out var known) || level < known.Item2) result[row.Item.RowId] = (job, level);
            }
        }
        return result;
    });

    private static readonly Lazy<HashSet<uint>> Fish = new(() =>
        Svc.Data.GetExcelSheet<FishParameter>().Select(f => f.Item.RowId).Concat(Svc.Data.GetExcelSheet<SpearfishingItem>().Select(s => s.Item.RowId)).Where(i => i != 0).ToHashSet());

    private static readonly Lazy<HashSet<uint>> Ventured = new(() =>
        Svc.Data.GetExcelSheet<RetainerTaskNormal>().Select(r => r.Item.RowId).Where(i => i != 0).ToHashSet());

    private static string? ExistingList(string name)
    {
        var lists = PluginTools.ReadPluginJson("Artisan", null)["NewCraftingLists"] as JsonArray ?? [];
        return lists.OfType<JsonObject>().FirstOrDefault(l => string.Equals(l["Name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase))?["ID"]?.ToString();
    }

    private static async Task WaitForArtisan(CancellationToken ct)
    {
        for (var i = 0; i < 40 && !CraftGatherTools.ArtisanLoaded; i++) await Task.Delay(250, ct).ConfigureAwait(false);
        await Task.Delay(1000, ct).ConfigureAwait(false); // let it finish loading its config and Raphael cache
    }

    private static unsafe bool HasGearset(string jobAbbr)
    {
        var job = Svc.Data.GetExcelSheet<ClassJob>().FirstOrDefault(j => j.Abbreviation.ExtractText() == jobAbbr);
        var module = RaptureGearsetModule.Instance();
        if (module == null || job.RowId == 0) return true;
        for (var i = 0; i < 100; i++)
            if (module->IsValidGearset(i) && module->GetGearset(i)->ClassJob == job.RowId) return true;
        return false;
    }
}
