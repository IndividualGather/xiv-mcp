using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.CustomDeliveries;

namespace XivMcp.Util;

/// <summary>
/// Satisfier (vsatisfy), which automates custom deliveries and offers no IPC: reflection into its window's client list (refreshed by
/// calling its own update, as drawing the window does) and into clib's automation, which runs its tasks (AutoCraft, AutoGather,
/// AutoFish for one client each). Framework thread.
/// </summary>
internal static class SatisfierBridge
{
    public const string PluginId = "vsatisfy";
    private static readonly ForeignPlugin Plugin = new(PluginId, "Satisfier");

    public static bool Loaded => Plugin.Loaded;

    /// <summary>One client as Satisfier sees it, with its own object for starting a task.</summary>
    public sealed record Client(object Info, int Index, string Name, bool Unlocked, int Rank, int Satisfaction, int SatisfactionMax,
                                int DeliveriesLeft, uint[] Items, bool[] Bonus, int[] Remaining, bool HasCraft, bool HasGather, bool HasFish,
                                bool SpearFish, uint FishSpot, uint[] Rewards)
    {
        public DeliveryNpc ForPlan => new(Name, Unlocked, Remaining, Enumerable.Range(0, 3).Where(i => Bonus[i]).Select(i => (DeliveryKind)i).ToHashSet());
    }

    private static object Window => Plugin.Get(Plugin.Self, "_wndMain") ?? throw Plugin.Unsupported("Plugin._wndMain");

    /// <summary>Every client, refreshed from the game.</summary>
    public static List<Client> Clients()
    {
        var window = Window;
        Plugin.Call(window, "UpdateData");
        var list = (IEnumerable)(Plugin.Get(window, "_npcs") ?? throw Plugin.Unsupported("MainWindow._npcs"));
        var clients = new List<Client>();
        foreach (var npc in list)
        {
            var max = Int(npc, "MaxDeliveries");
            var used = Int(npc, "UsedDeliveries");
            var fish = Plugin.Get(npc, "FishData");
            var remaining = Enumerable.Range(0, 3).Select(i => Convert.ToInt32(Plugin.Call(npc, "RemainingTurnins", i))).ToArray();
            var index = Int(npc, "Index");
            clients.Add(new Client(npc, index, Plugin.Get(npc, "Name") as string ?? "?", IsUnlocked(index),
                Int(npc, "Rank"), Int(npc, "SatisfactionCur"), Int(npc, "SatisfactionMax"), Math.Max(0, max - used),
                (uint[])(Plugin.Get(npc, "TurnInItems") ?? new uint[3]), (bool[])(Plugin.Get(npc, "IsBonusEffective") ?? new bool[3]), remaining,
                Plugin.Get(npc, "CraftData") is not null, Plugin.Get(npc, "GatherData") is not null, fish is not null,
                fish is not null && Plugin.Get(fish, "IsSpearFish") is true,
                fish is null ? 0 : Convert.ToUInt32(Plugin.Get(fish, "FishSpotId")),
                (uint[])(Plugin.Get(npc, "Rewards") ?? new uint[3])));
        }
        return clients;
    }

    private static int Int(object o, string member) => Convert.ToInt32(Plugin.Get(o, member));

    /// <summary>
    /// Whether the client's unlock quest is done. Satisfier's own flag looks at the previous client's row (its index is the row id
    /// minus one), so XIV MCP reads the right row itself.
    /// </summary>
    private static bool IsUnlocked(int index) =>
        Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SatisfactionNpc>().GetRowOrDefault((uint)index + 1) is { } row
        && QuestManager.IsQuestComplete(row.QuestRequired.RowId);

    /// <summary>
    /// The game's custom delivery agent as Satisfier judges it ("is the turn-in window open?"): whether it is active, for which
    /// client, and which focused window its addon id points at. Satisfier calls the turn-in window open when that window is visible,
    /// without checking that it is the turn-in window. Framework thread.
    /// </summary>
    public static unsafe object TurnInAgent()
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentSatisfactionSupply.Instance();
        if (agent == null) return new { available = false };
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* focused = null;
        ref var list = ref FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance()->RaptureAtkUnitManager->AtkUnitManager.FocusedUnitsList;
        for (var i = 0; i < Math.Min((int)list.Count, list.Entries.Length); i++)
            if (list.Entries[i].Value is var unit && unit != null && unit->Id == agent->AddonId) { focused = unit; break; }
        return new
        {
            active = agent->IsAgentActive(),
            clientIndex = agent->NpcInfo.Id - 1,
            valid = agent->NpcInfo.Valid,
            initialized = agent->NpcInfo.Initialized,
            addonId = agent->AddonId,
            focusedWindow = focused == null ? null : focused->NameString,
            focusedWindowVisible = focused != null && focused->IsVisible,
        };
    }

    /// <summary>
    /// The capped currencies one delivery of a request pays (scrips), at the highest collectability tier, with what the player has.
    /// The reward row is the one Satisfier resolved (the bonus row when the bonus applies). Framework thread.
    /// </summary>
    public static unsafe List<CustomDeliveries.CappedReward> Rewards(Client client, DeliveryKind kind)
    {
        var rewards = new List<CustomDeliveries.CappedReward>();
        if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SatisfactionSupplyReward>().GetRowOrDefault(client.Rewards[(int)kind]) is not { } row) return rewards;
        var cm = CurrencyManager.Instance();
        var level = Svc.Objects.LocalPlayer?.Level ?? 0;
        for (var i = 0; i < row.SatisfactionSupplyRewardData.Count; i++)
        {
            var data = row.SatisfactionSupplyRewardData[i];
            if (data.RewardCurrency == 0 || (i == 1 && level < row.MinLevelForSecondReward)) continue;
            var itemId = cm->GetItemIdBySpecialId((byte)data.RewardCurrency);
            if (itemId == 0) continue;
            rewards.Add(new CustomDeliveries.CappedReward(Util.Items.Name(itemId), cm->GetItemCount(itemId), cm->GetItemMaxCount(itemId),
                data.QuantityHigh * row.BonusMultiplier / 100));
        }
        return rewards;
    }

    /// <summary>Deliveries left this week for all clients together.</summary>
    public static unsafe int Allowances() => SatisfactionSupplyManager.Instance()->GetRemainingAllowances();

    private static object Automation =>
        Plugin.StaticValue(Plugin.Library("clib"), "clib.Services.Svc", "Automation") ?? throw Plugin.Unsupported("clib's automation");

    /// <summary>Starts Satisfier's task for the client: AutoCraft, AutoGather or AutoFish.</summary>
    public static void Start(Client client, DeliveryKind kind)
    {
        var type = Plugin.Type(kind switch { DeliveryKind.Craft => "Satisfy.AutoCraft", DeliveryKind.Gather => "Satisfy.AutoGather", _ => "Satisfy.AutoFish" });
        var task = Activator.CreateInstance(type, client.Info) ?? throw Plugin.Unsupported(type.Name);
        Plugin.Call(Automation, "Start", task, null, false);
    }

    public static bool Running => Plugin.Get(Automation, "Running") is true;

    public static string Status => Plugin.Get(Automation, "Status") as string ?? "";

    public static void Stop() => Plugin.Call(Automation, "Stop");

    /// <summary>Keeps Satisfier's window open until disposed: it refreshes the client data its tasks rely on while drawn.</summary>
    public static IDisposable KeepWindowOpen() => Plugin.Override(Window, ("IsOpen", true));
}
