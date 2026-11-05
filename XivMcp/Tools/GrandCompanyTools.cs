using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Grand Company expert delivery with AutoRetainer's own delivery feature: XIV MCP checks AutoRetainer's settings for the character
/// (only "Hide Armoury Chest Items", so the armoury chest and gear sets are never touched), walks to the Personnel Officer, has
/// AutoRetainer buy and deliver (its exchange plan says what it buys with the seals), waits until it is done and reports what left
/// the bags. XIV MCP doesn't hand anything in itself.
/// </summary>
internal static unsafe class GrandCompanyTools
{
    private const uint Ventures = 21072;

    /// <summary>The Personnel Officer of each Grand Company (PlayerState.GrandCompany: 1 Maelstrom, 2 Twin Adder, 3 Immortal Flames).</summary>
    private static readonly string[] Officers = ["", "Storm Personnel Officer", "Serpent Personnel Officer", "Flame Personnel Officer"];

    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    public static IEnumerable<McpTool> Create(Configuration config, PluginCompat compat)
    {
        yield return new McpTool
        {
            Name = "deliver_to_grand_company",
            Description = "Grand Company expert delivery with AutoRetainer's delivery feature: walks to your Personnel Officer and lets " +
                          "AutoRetainer hand in the eligible gear from your bags for seals and buy what its exchange plan lists (Venture " +
                          "coins, say), also when the seals would cap. Only runs when AutoRetainer's delivery setting for the character is " +
                          "\"Hide Armoury Chest Items\", so nothing from the armoury chest or a gear set is handed in. Waits until AutoRetainer " +
                          "is done and reports the items that left the bags, seals and Ventures. Requires AutoRetainer, and 'Items & " +
                          "retainers' in /xivmcp.",
            InputSchema = """
                { "type": "object", "properties": { "wait_seconds": { "type": "integer", "minimum": 30, "maximum": 3600, "description": "How long to wait for AutoRetainer to finish (default 900)." } } }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (!config.AllowItemsRetainers)
                    throw new ToolException("Grand Company delivery is disabled. Enable \"Items & retainers\" in the XIV MCP settings window (/xivmcp) in game.");
                var wait = TimeSpan.FromSeconds(args.Int("wait_seconds", 900, 30, 3600));
                var steps = new List<string>();

                var start = await Game.RunLoggedIn(() =>
                {
                    InventoryActionTools.EnsureNotBusy();
                    if (!compat.AutoRetainerLoaded) throw new ToolException("AutoRetainer isn't loaded; it does the expert delivery.");
                    if (compat.AutoRetainerMultiMode) throw new ToolException("AutoRetainer is in multi mode; turn it off first.");
                    if (compat.AutoRetainerBusy) throw new ToolException("AutoRetainer is busy right now; try again when it is done.");
                    var gc = GrandCompany();
                    if (gc is 0 or > 3) throw new ToolException("The character isn't in a Grand Company.");
                    var settings = AutoRetainerSettings();
                    if (ExpertDelivery.Refusal(settings, Svc.PlayerState.ContentId) is { } refusal) throw new ToolException(refusal);
                    return (Officer: Officers[gc], Gc: gc, Plan: ExpertDelivery.Plan(settings), Bags: BagCounts(), Seals: Seals(gc), Ventures: Count(Ventures));
                }).ConfigureAwait(false);

                // AutoRetainer interacts only within reach of the Personnel Officer and the quartermaster next to it.
                var territory = await Game.Run(() => (uint)Svc.ClientState.TerritoryType).ConfigureAwait(false);
                var spot = await Task.Run(() => TriadData.FindNpc(start.Officer, territory), ct).ConfigureAwait(false)
                           ?? throw new ToolException($"No place for the {start.Officer} in the game data.");
                await NavigationTools.GoToNpc(spot, steps, ct).ConfigureAwait(false);

                await Game.Run(() => { Svc.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.GC.EnqueueInitiation").InvokeAction(); return true; }).ConfigureAwait(false);
                steps.Add("AutoRetainer: buying and delivering.");

                // AutoRetainer works in steps (buy, talk, deliver, buy again): done once it and the delivery windows have been idle a while.
                bool Working() => compat.AutoRetainerBusy || Svc.Condition[ConditionFlag.OccupiedInQuestEvent] || Svc.Condition[ConditionFlag.OccupiedInEvent]
                                  || GameWindows.Ready("GrandCompanySupplyList") || GameWindows.Ready("GrandCompanyExchange");
                var began = await GameWindows.WaitFor(Working, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                if (!began) throw new ToolException("AutoRetainer didn't start the delivery. Check that the character's Grand Company rank allows expert delivery.");
                var idleSince = DateTime.MaxValue;
                var done = await GameWindows.WaitFor(() =>
                {
                    if (Working()) { idleSince = DateTime.MaxValue; return false; }
                    if (idleSince == DateTime.MaxValue) idleSince = DateTime.UtcNow;
                    return DateTime.UtcNow - idleSince > TimeSpan.FromSeconds(6);
                }, wait, ct).ConfigureAwait(false);

                return await Game.Run(() =>
                {
                    var gone = ExpertDelivery.Gone(start.Bags, BagCounts());
                    gone.Remove(Ventures);
                    return (object)new
                    {
                        done,
                        handedIn = gone.Select(g => new { item = Items.Name(g.Key), quantity = g.Value }).ToList(),
                        seals = new { before = start.Seals, after = Seals(start.Gc), max = MaxSeals(start.Gc) },
                        ventures = new { before = start.Ventures, after = Count(Ventures) },
                        exchangePlan = start.Plan.Buys.Select(Items.Name).ToList(),
                        steps,
                        note = done ? null : "AutoRetainer is still working; check get_automation_status and get_inventory later.",
                    };
                }).ConfigureAwait(false);
            },
        };
    }

    /// <summary>AutoRetainer's settings file (pluginConfigs/AutoRetainer/DefaultConfig.json), as saved.</summary>
    private static string AutoRetainerSettings()
    {
        var path = Path.Combine(Svc.PluginInterface.ConfigDirectory.Parent!.FullName, "AutoRetainer", "DefaultConfig.json");
        if (!File.Exists(path)) throw new ToolException("AutoRetainer's settings file wasn't found.");
        return File.ReadAllText(path);
    }

    private static unsafe Dictionary<uint, int> BagCounts()
    {
        var im = InventoryManager.Instance();
        var counts = new Dictionary<uint, int>();
        foreach (var type in Bags)
        {
            var c = im->GetInventoryContainer(type);
            for (var i = 0; c != null && i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                counts[slot->ItemId] = counts.GetValueOrDefault(slot->ItemId) + (int)slot->Quantity;
            }
        }
        return counts;
    }

    private static unsafe byte GrandCompany() => PlayerState.Instance()->GrandCompany;
    private static unsafe int Count(uint itemId) => InventoryManager.Instance()->GetInventoryItemCount(itemId);
    private static unsafe uint Seals(byte gc) => InventoryManager.Instance()->GetCompanySeals(gc);
    private static unsafe uint MaxSeals(byte gc) => InventoryManager.Instance()->GetMaxCompanySeals(gc);
}
