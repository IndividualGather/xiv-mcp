using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Fishing;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Fishing with AutoHook (integration "AutoHook"): a preset XIV MCP builds from the fishing data (bait, mooches, which bites to
/// hook and how, Patience, a timeout after the bite window), AutoHook switched on to hook and cast again, and a job that does it
/// all: look the fish up, switch to Fisher, buy bait, travel to the spot, wait for the window and fish until it is caught.
/// AutoHook's IPC: SetPluginState, CreateAndSelectAnonymousPreset (an exported preset string) and DeleteAllAnonymousPresets.
/// </summary>
internal static class AutoHookTools
{
    internal const string PluginId = "AutoHook";

    public static IEnumerable<McpTool> Create(Configuration config, Func<JobManager> jobs, Func<string?> client)
    {
        yield return new McpTool
        {
            Name = "set_autohook_preset",
            Description = "Builds an AutoHook preset for catching a fish and selects it in AutoHook (as a temporary preset, without " +
                          "changing the player's own presets): the bait (swapped to automatically), mooching the fish on the way, " +
                          "hooking only the tug of the fish each cast is for with its hookset, the lure the fish needs, Patience II " +
                          "for big fish, Collect and Snagging when needed, the fish for Fisher's Intuition first when they bite on the same bait, and reeling in when the fish has not bitten by the end of its bite window. Does not " +
                          "start fishing; fish_until does. Uses the same data as find_fish, so it also needs 'Online lookups'.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "fish": { "type": "string", "description": "The fish's name or item id." },
                    "spot": { "type": "string", "description": "The fishing spot's name or id (default: the spot in the current zone, else the best one)." }
                  },
                  "required": ["fish"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var (guide, _) = await Prepare(args, config, ct).ConfigureAwait(false);
                return await Select(guide).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "fish_until",
            Description = "Fishes with AutoHook until the bags hold 'quantity' more of a fish: selects the preset for it (as " +
                          "set_autohook_preset), waits for the fish's time and weather window if it is not open, switches AutoHook on " +
                          "and casts. AutoHook hooks, mooches and casts again; XIV MCP counts the catches. Stops when caught, when the " +
                          "window closes ('stop_when_window_closes', default true), at the timeout, or when the job is paused or " +
                          "cancelled; then AutoHook is switched off, the line is reeled in and the temporary preset is removed. The " +
                          "character must be a Fisher at the fishing spot's water (navigate_to with destination fishing_spot), with the " +
                          "bait in the bags. Meant as a job step; catch_fish builds the whole job. Requires 'Online lookups' too.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "fish": { "type": "string", "description": "The fish's name or item id." },
                    "quantity": { "type": "integer", "minimum": 1, "maximum": 999, "description": "How many more to catch (default 1)." },
                    "spot": { "type": "string", "description": "The fishing spot's name or id (default: the one in the current zone)." },
                    "stop_when_window_closes": { "type": "boolean", "description": "Stop when the fish's window closes (default true). Waits for the next window first if none is open." },
                    "timeout_minutes": { "type": "integer", "minimum": 1, "maximum": 1440, "description": "Give up after this long, waiting included (default 120)." }
                  },
                  "required": ["fish"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) => await FishUntil(args, config, ct).ConfigureAwait(false),
        };

        yield return new McpTool
        {
            Name = "catch_fish",
            Description = "Starts a background job that catches a fish from start to finish: find_fish (the data and the plan), " +
                          "switch_gearset to Fisher, buy_item for the bait when the bags have none (gil vendors only), navigate_to the " +
                          "fishing spot, and fish_until with AutoHook, which waits for the fish's window. Returns the job; follow it " +
                          "with get_job. Each step follows its own setting in /xivmcp (Online lookups, Game & navigation, Market & " +
                          "purchases, AutoHook).",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "fish": { "type": "string", "description": "The fish's name or item id." },
                    "quantity": { "type": "integer", "minimum": 1, "maximum": 999, "description": "How many to catch (default 1)." },
                    "spot": { "type": "string", "description": "The fishing spot's name or id (default: the best one)." },
                    "bait_quantity": { "type": "integer", "minimum": 0, "maximum": 999, "description": "How much bait to buy if the bags have none (default 10 per fish, at most 99; 0: do not buy)." },
                    "timeout_minutes": { "type": "integer", "minimum": 1, "maximum": 1440, "description": "How long fish_until may take, waiting for the window included (default 240)." }
                  },
                  "required": ["fish"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var (guide, _) = await Prepare(args, config, ct, preferCurrentZone: false).ConfigureAwait(false);
                var fishName = Items.Name(guide.Fish);
                var quantity = args.Int("quantity", 1, 1, 999);
                // Ten casts' worth per fish wanted: some baits cost hundreds of gil.
                var baitQuantity = args.Int("bait_quantity", Math.Clamp(quantity * 10, 10, 99), 0, 999);
                var (haveBait, isFisher) = await Game.Run(() => (Items.CountInBags(guide.FirstBait) > 0, Svc.Objects.LocalPlayer?.ClassJob.RowId == NavigationTools.FisherJob))
                                                     .ConfigureAwait(false);

                var steps = new List<JobManager.Step>
                {
                    new() { Id = "fish", Tool = "find_fish", Args = new JsonObject { ["fish"] = guide.Fish.ToString(), ["spot"] = guide.Spot.ToString() },
                            Note = $"How to catch {fishName}" },
                };
                if (!isFisher) steps.Add(new() { Id = "job", Tool = "switch_gearset", Args = new JsonObject { ["gearset"] = "FSH" }, Note = "Switch to Fisher" });
                if (!haveBait && baitQuantity > 0)
                    steps.Add(new() { Id = "bait", Tool = "buy_item", Args = new JsonObject { ["item"] = guide.FirstBait.ToString(), ["quantity"] = baitQuantity },
                                      Note = $"Buy {baitQuantity} {Items.Name(guide.FirstBait)}" });
                steps.Add(new() { Id = "travel", Tool = "navigate_to", Args = new JsonObject { ["destination"] = "fishing_spot", ["name"] = guide.Spot.ToString() },
                                  Note = $"Go to {FishingTools.SpotName(guide.Spot)}" });
                steps.Add(new()
                {
                    Id = "catch", Tool = "fish_until",
                    Args = new JsonObject
                    {
                        ["fish"] = guide.Fish.ToString(), ["quantity"] = quantity, ["spot"] = guide.Spot.ToString(),
                        ["timeout_minutes"] = args.Int("timeout_minutes", 240, 1, 1440),
                    },
                    Note = $"Catch {quantity} {fishName} with AutoHook",
                });

                var job = jobs().Start($"Fishing: {quantity} {fishName}", steps, client());
                return new
                {
                    started = JobManager.Describe(job),
                    spot = FishingTools.SpotName(guide.Spot),
                    bait = Items.Name(guide.FirstBait),
                    buysBait = !haveBait && baitQuantity > 0,
                    warnings = Warnings(guide, haveBait, baitQuantity),
                };
            },
        };

        yield return new McpTool
        {
            Name = "stop_fishing",
            Description = "Switches AutoHook off and reels the line in. A running fish_until step stops too when its job is paused or cancelled.",
            ReadOnly = false,
            Handler = async (_, _) =>
            {
                await StopAll().ConfigureAwait(false);
                return new { stopped = true };
            },
        };
    }

    // ------------------------------------------------------------------ the steps

    /// <summary>The guide for the fish in the arguments, at the given spot, the spot in the current zone, or the best one.</summary>
    private static async Task<(FishGuide Guide, FishingSources.Data Data)> Prepare(ToolArgs args, Configuration config, CancellationToken ct, bool preferCurrentZone = true)
    {
        ItemSourceTools.RequireOnline(config);
        var fish = await Task.Run(() => FishingTools.ResolveFish(args.String("fish") ?? throw new ToolException("'fish' is required.")), ct).ConfigureAwait(false);
        var data = await FishingSources.Get(ct).ConfigureAwait(false);
        uint? spot = args.String("spot") is { } s ? await Task.Run(() => FishingTools.ResolveSpotName(s), ct).ConfigureAwait(false) : null;
        if (spot is null && preferCurrentZone)
        {
            var territory = await Game.Run(() => (uint)Svc.ClientState.TerritoryType).ConfigureAwait(false);
            spot = FishGuides.Spots(fish, data.Tracker, data.Sources)
                .Where(id => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.FishingSpot>().GetRowOrDefault(id)?.TerritoryType.RowId == territory)
                .Select(id => (uint?)id).FirstOrDefault();
        }
        return (await FishingTools.Guide(fish, spot, data, ct).ConfigureAwait(false), data);
    }

    /// <summary>Hands AutoHook the preset for a guide and selects it.</summary>
    private static async Task<object> Select(FishGuide guide)
    {
        var name = $"XIV MCP: {Items.Name(guide.Fish)}";
        var export = AutoHookPreset.Export(AutoHookPreset.Build(guide, name));
        await Game.Run(() =>
        {
            RequireAutoHook();
            Svc.PluginInterface.GetIpcSubscriber<string, object>("AutoHook.CreateAndSelectAnonymousPreset").InvokeAction(export);
            return true;
        }).ConfigureAwait(false);
        return new
        {
            preset = name,
            bait = Items.Name(guide.FirstBait),
            casts = guide.Path.Select(s => new
            {
                castWith = Items.Name(s.Bait), mooch = s.Mooch, hooks = s.Tug is { } t ? Tugs.Describe(t) : "every bite",
                hookset = s.Hookset?.ToString(), reelsInAfterSeconds = s.Fish == guide.Fish && s.Bite is { } b ? Math.Ceiling(b.Max) + 1 : (double?)null,
            }),
            patience = guide.BigFish,
            collect = guide.Collectable,
            snagging = guide.Snagging,
            lure = guide.Lure is { } l ? $"{l} Lure" : null,
            fishersIntuition = guide.Predators.Count == 0 ? null : AutoHookPreset.CatchesPredators(guide)
                ? $"AutoHook first catches {Predators(guide)} on the same bait; once Fisher's Intuition is up, it only hooks {Items.Name(guide.Fish)}."
                : $"Catch {Predators(guide)} yourself first (see find_fish for how); the preset only hooks {Items.Name(guide.Fish)} while Fisher's Intuition is up.",
        };
    }

    /// <summary>What the job cannot do on its own, for the player to know before it starts.</summary>
    private static List<string> Warnings(FishGuide g, bool haveBait, int baitQuantity)
    {
        var bait = Items.Name(g.FirstBait);
        var list = new List<string>();
        if (!haveBait && baitQuantity == 0) list.Add($"You have no {bait}; the fishing step fails without it.");
        else if (!haveBait && !FishingTools.SoldForGil(g.FirstBait)) list.Add($"{bait} is not sold for gil, so the bait step will fail; get it first (see get_item_sources).");
        if (g.Folklore is not null) list.Add($"{Items.Name(g.Fish)} only bites once you have read its folklore tome.");
        if (g.Predators.Count > 0 && !AutoHookPreset.CatchesPredators(g))
            list.Add($"Catch {Predators(g)} yourself first for Fisher's Intuition; they do not bite on {bait}.");
        return list;
    }

    private static string Predators(FishGuide g) => string.Join(", ", g.Predators.Select(p => $"{p.Count} {Items.Name(p.Fish)}"));

    private static async Task<object?> FishUntil(ToolArgs args, Configuration config, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var timeout = TimeSpan.FromMinutes(args.Int("timeout_minutes", 120, 1, 1440));
        var quantity = args.Int("quantity", 1, 1, 999);
        var stopWithWindow = args.Node("stop_when_window_closes")?.GetValue<bool>() ?? true;
        var (guide, _) = await Prepare(args, config, ct).ConfigureAwait(false);
        var fishName = Items.Name(guide.Fish);

        var (before, territory) = await Game.RunLoggedIn(() =>
        {
            RequireAutoHook();
            if (Svc.Objects.LocalPlayer!.ClassJob.RowId != NavigationTools.FisherJob) throw new ToolException("Switch to Fisher first (switch_gearset).");
            if (Items.CountInBags(guide.FirstBait) == 0) throw new ToolException($"No {Items.Name(guide.FirstBait)} in your bags. Buy some with buy_item.");
            return (Items.CountInBags(guide.Fish), (uint)Svc.ClientState.TerritoryType);
        }).ConfigureAwait(false);
        var spotTerritory = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.FishingSpot>().GetRowOrDefault(guide.Spot)?.TerritoryType.RowId ?? 0;
        if (spotTerritory != 0 && spotTerritory != territory)
            throw new ToolException($"{FishingTools.SpotName(guide.Spot)} is in {NavigationTools.TerritoryName(spotTerritory)}. Go there first (navigate_to with destination fishing_spot).");

        var rates = FishingTools.Rates(spotTerritory);
        var anyTime = guide.Conditions.AnyTime || rates.Count == 0;
        if (!anyTime && FishWindows.Next(guide.Conditions, rates, DateTimeOffset.UtcNow, 1, TimeSpan.FromDays(30)).Count == 0)
            throw new ToolException($"{fishName} has no window in the next 30 days.");
        var run = new FishingRun(new FishGuideNames(fishName, Items.Name(guide.FirstBait), FishingTools.SpotName(guide.Spot)), quantity,
            DateTimeOffset.UtcNow, timeout, stopWithWindow,
            now => anyTime ? null : FishWindows.Next(guide.Conditions, rates, now, 1, TimeSpan.FromDays(30)).FirstOrDefault());

        var log = new List<string>();
        var selected = await Select(guide).ConfigureAwait(false);
        try
        {
            var wait = run.WaitBeforeFishing(DateTimeOffset.UtcNow, out var tooLate);
            if (tooLate is not null) throw new ToolException(tooLate);
            if (wait > TimeSpan.Zero)
            {
                log.Add($"Waited for the window at {DateTimeOffset.Now.Add(wait):HH:mm}.");
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            await Game.Run(() => { SetAutoHook(true); return true; }).ConfigureAwait(false);
            log.Add("AutoHook on.");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var state = await Game.Run(() => new FishingState(DateTimeOffset.UtcNow, Items.CountInBags(guide.Fish) - before,
                        Svc.Condition[ConditionFlag.Fishing], NavigationTools.CanCast(), Items.CountInBags(guide.FirstBait), Items.FreeBagSlots()))
                    .ConfigureAwait(false);
                var decision = run.Next(state);
                switch (decision.Action)
                {
                    case FishingAction.Done:
                        return new { done = true, caught = state.Caught, fish = fishName, minutes = Math.Round((DateTime.UtcNow - started).TotalMinutes, 1), preset = selected, log };
                    case FishingAction.Stop:
                        throw new ToolException(decision.Reason!);
                    case FishingAction.Cast:
                        await Game.Run(() => { UseAction(NavigationTools.CastAction); return true; }).ConfigureAwait(false);
                        break;
                }
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await StopAll(removePresets: true).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ AutoHook and the game

    internal static bool Loaded => PluginCompat.IsLoaded(PluginId);

    private static void RequireAutoHook()
    {
        if (!Loaded) throw new ToolException("AutoHook is not loaded.");
    }

    private static void SetAutoHook(bool on) =>
        Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoHook.SetPluginState").InvokeAction(on);

    private static unsafe void UseAction(uint id) => ActionManager.Instance()->UseAction(ActionType.Action, id);

    /// <summary>AutoHook off and the line reeled in; with <paramref name="removePresets"/>, AutoHook's temporary presets are deleted too.</summary>
    private static async Task StopAll(bool removePresets = false)
    {
        await Game.Run(() =>
        {
            if (!Loaded) return true;
            try
            {
                SetAutoHook(false);
                if (removePresets) Svc.PluginInterface.GetIpcSubscriber<object>("AutoHook.DeleteAllAnonymousPresets").InvokeAction();
            }
            catch (Exception ex) { Svc.Log.Warning($"[MCP] AutoHook: {ex.Message}"); }
            return true;
        }).ConfigureAwait(false);
        // Reel in once nothing is on the hook (Quit fails while a fish is being landed).
        for (var i = 0; i < 10 && await Game.Run(() => Svc.Condition[ConditionFlag.Fishing]).ConfigureAwait(false); i++)
        {
            await Game.Run(() => { UseAction(NavigationTools.QuitAction); return true; }).ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);
        }
    }
}
