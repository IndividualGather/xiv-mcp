using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Inventory;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Free company chest: read its contents, and — when FCCH is installed — deposit/withdraw through FCCH's IPC, so its own
/// rules, logging and pacing apply instead of XIV MCP sending competing moves.
/// </summary>
internal static class FcChestTools
{
    private static readonly GameInventoryType[] ChestPages =
        [GameInventoryType.FreeCompanyPage1, GameInventoryType.FreeCompanyPage2, GameInventoryType.FreeCompanyPage3,
         GameInventoryType.FreeCompanyPage4, GameInventoryType.FreeCompanyPage5];

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        yield return new McpTool
        {
            Name = "get_fc_chest",
            Description = "Contents of the free company chest (item tabs, crystals and gil): live while the game has it loaded, otherwise the last " +
                          "snapshot with its age (the game only sends the chest when it is opened). " +
                          "Filter by item name.",
            InputSchema = """
                { "type": "object", "properties": { "query": { "type": "string", "description": "Only items whose name contains this text." } } }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                var query = args.String("query");
                if (!StorageTracker.IsLoaded("fc_chest"))
                {
                    // Not loaded right now: answer from the last snapshot, with its age.
                    var group = StorageTracker.Groups["fc_chest"];
                    var stored = StorageTracker.Instance?.Get("fc_chest")
                        ?? throw new ToolException("The free company chest is not loaded and was never captured. To capture it: " + group.Hint);
                    return new
                    {
                        gil = stored.Gil,
                        tabs = stored.Items.Where(i => i.Container.StartsWith("FreeCompanyPage", StringComparison.Ordinal))
                            .Select(i => (i, name: InventoryTools.ItemName(i.ItemId))).Where(x => Game.Matches(x.name, query))
                            .GroupBy(x => x.i.Container).OrderBy(g => g.Key)
                            .Select(g => new { tab = g.Key, items = g.Select(x => new { itemId = x.i.ItemId, name = x.name, quantity = x.i.Quantity, hq = x.i.Hq ? true : (bool?)null }).ToList() })
                            .ToList(),
                        crystals = stored.Items.Where(i => i.Container == "FreeCompanyCrystals")
                            .Select(i => new { itemId = i.ItemId, name = InventoryTools.ItemName(i.ItemId), quantity = i.Quantity }).ToList(),
                        cache = CacheFreshness.Describe(stored.CapturedUtc, false, group.Hint),
                        fcch = PluginCompat.FcchLoaded ? "installed: use fc_chest_transfer to deposit or withdraw" : null,
                    };
                }
                var tabs = ChestPages.Select(page => new
                {
                    tab = page.ToString(),
                    items = Svc.Inventory.GetInventoryItems(page).ToArray().Where(i => !i.IsEmpty)
                        .Select(i => (i, name: InventoryTools.ItemName(i.BaseItemId)))
                        .Where(x => Game.Matches(x.name, query))
                        .Select(x => new { itemId = x.i.BaseItemId, name = x.name, quantity = x.i.Quantity, hq = x.i.IsHq ? true : (bool?)null })
                        .ToList(),
                }).ToList();
                var crystals = Svc.Inventory.GetInventoryItems(GameInventoryType.FreeCompanyCrystals).ToArray().Where(i => !i.IsEmpty)
                    .Select(i => new { itemId = i.BaseItemId, name = InventoryTools.ItemName(i.BaseItemId), quantity = i.Quantity }).ToList();
                unsafe
                {
                    var gil = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance()->GetFreeCompanyGil();
                    return new { gil, tabs, crystals, fcch = PluginCompat.FcchLoaded ? "installed: use fc_chest_transfer to deposit or withdraw" : null };
                }
            }),
        };

        yield return new McpTool
        {
            Name = "fc_chest_transfer",
            Description = "Deposits to or withdraws from the free company chest through FCCH (must be installed). FCCH opens the chest itself when " +
                          "you are standing at it. action: deposit | withdraw | withdraw_missing (withdraw up to the given amounts) | stop. " +
                          "Give 'items' as {\"itemId\": quantity}, or a preset: all, duplicates (deposit only), custom (your FCCH list), workshop " +
                          "(withdraw workshop materials), or 'gil' with an amount like 15k, 5m, 50% or all. Waits until FCCH is done. " +
                          "Requires 'Allow inventory actions' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "action": { "type": "string", "enum": ["deposit", "withdraw", "withdraw_missing", "stop"] },
                    "items": { "type": "object", "description": "Item id → quantity, e.g. {\"5111\": 999}." },
                    "preset": { "type": "string", "enum": ["all", "duplicates", "custom", "workshop"] },
                    "gil": { "type": "string", "description": "Gil amount: e.g. 15k, 5m, 50%, all." },
                    "timeout_seconds": { "type": "integer", "description": "How long to wait for FCCH to finish (default 180, max 900)." }
                  },
                  "required": ["action"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (!config.AllowInventoryActions)
                    throw new ToolException("Inventory actions are disabled. Enable \"Allow inventory actions\" in the XIV MCP settings window (/xivmcp) in game.");
                if (!PluginCompat.FcchLoaded)
                    throw new ToolException("Free company chest transfers use FCCH, which is not installed. Without it, open the chest and move items in game.");

                var action = args.String("action")?.ToLowerInvariant() ?? throw new ToolException("'action' is required.");
                if (action == "stop")
                {
                    var stopped = await Game.Run(() => Svc.PluginInterface.GetIpcSubscriber<bool>("FCCH.Stop").InvokeFunc()).ConfigureAwait(false);
                    return new { stopped };
                }

                var items = ParseItems(args.Node("items"));
                var preset = args.String("preset")?.ToLowerInvariant();
                var gil = args.String("gil");
                var given = (items is null ? 0 : 1) + (preset is null ? 0 : 1) + (gil is null ? 0 : 1);
                if (given != 1) throw new ToolException("Give exactly one of 'items', 'preset' or 'gil'.");

                var (ipc, call) = (action, items, preset, gil) switch
                {
                    ("deposit", not null, _, _) => ("FCCH.DepositItems", Call<Dictionary<uint, int>>("FCCH.DepositItems", items)),
                    ("withdraw", not null, _, _) => ("FCCH.WithdrawItems", Call<Dictionary<uint, int>>("FCCH.WithdrawItems", items)),
                    ("withdraw_missing", not null, _, _) => ("FCCH.WithdrawMissingItems", Call<Dictionary<uint, int>>("FCCH.WithdrawMissingItems", items)),
                    ("deposit", _, "all", _) => ("FCCH.DepositAll", Call("FCCH.DepositAll")),
                    ("deposit", _, "duplicates", _) => ("FCCH.DepositDuplicates", Call("FCCH.DepositDuplicates")),
                    ("deposit", _, "custom", _) => ("FCCH.DepositCustom", Call("FCCH.DepositCustom")),
                    ("withdraw", _, "all", _) => ("FCCH.WithdrawAll", Call("FCCH.WithdrawAll")),
                    ("withdraw", _, "custom", _) => ("FCCH.WithdrawCustom", Call("FCCH.WithdrawCustom")),
                    ("withdraw", _, "workshop", _) => ("FCCH.WithdrawWorkshop", Call("FCCH.WithdrawWorkshop")),
                    ("deposit", _, _, not null) => ("FCCH.DepositGil", Call<string>("FCCH.DepositGil", gil)),
                    ("withdraw", _, _, not null) => ("FCCH.WithdrawGil", Call<string>("FCCH.WithdrawGil", gil)),
                    _ => throw new ToolException($"'{action}' can't be combined with that ({(preset ?? (gil is not null ? "gil" : "items"))})."),
                };

                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var accepted = await Game.RunLoggedIn(() =>
                    {
                        if (PluginCompat.FcchBusy) throw new ToolException("FCCH is already busy with another chest operation.");
                        return call();
                    }).ConfigureAwait(false);
                    if (!accepted)
                        return new { accepted = false, via = ipc, note = "FCCH refused the command (e.g. not at the company chest, invalid amount, or no access to that chest tab). Its chat message has details." };

                    // Wait until FCCH has finished its queued moves.
                    var started = DateTime.UtcNow;
                    var timeout = TimeSpan.FromSeconds(args.Int("timeout_seconds", 180, 10, 900));
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    var finished = false;
                    while (DateTime.UtcNow - started < timeout)
                    {
                        if (!await Game.Run(() => PluginCompat.FcchBusy).ConfigureAwait(false)) { finished = true; break; }
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                    return new
                    {
                        accepted = true,
                        via = ipc,
                        finished,
                        seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
                        note = finished ? "Done. Use get_fc_chest / get_inventory to see the result." : "FCCH is still working; check again later or use action=stop.",
                    };
                }
                finally { InventoryActionTools.Gate.Release(); }
            },
        };
    }

    private static Func<bool> Call(string name) => () => Svc.PluginInterface.GetIpcSubscriber<bool>(name).InvokeFunc();

    private static Func<bool> Call<T>(string name, T arg) => () => Svc.PluginInterface.GetIpcSubscriber<T, bool>(name).InvokeFunc(arg);

    private static Dictionary<uint, int>? ParseItems(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonObject o || o.Count == 0) throw new ToolException("'items' must be an object like {\"5111\": 999}.");
        var result = new Dictionary<uint, int>();
        foreach (var (key, value) in o)
        {
            if (!uint.TryParse(key, out var id) || value is not JsonValue v || !v.TryGetValue<int>(out var qty) || qty <= 0)
                throw new ToolException($"Invalid entry '{key}': use item id → positive quantity.");
            result[id] = qty;
        }
        return result;
    }
}
