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
                                bool SpearFish, uint FishSpot)
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
            clients.Add(new Client(npc, Int(npc, "Index"), Plugin.Get(npc, "Name") as string ?? "?", Plugin.Get(npc, "Unlocked") is true,
                Int(npc, "Rank"), Int(npc, "SatisfactionCur"), Int(npc, "SatisfactionMax"), Math.Max(0, max - used),
                (uint[])(Plugin.Get(npc, "TurnInItems") ?? new uint[3]), (bool[])(Plugin.Get(npc, "IsBonusEffective") ?? new bool[3]), remaining,
                Plugin.Get(npc, "CraftData") is not null, Plugin.Get(npc, "GatherData") is not null, fish is not null,
                fish is not null && Plugin.Get(fish, "IsSpearFish") is true,
                fish is null ? 0 : Convert.ToUInt32(Plugin.Get(fish, "FishSpotId"))));
        }
        return clients;
    }

    private static int Int(object o, string member) => Convert.ToInt32(Plugin.Get(o, member));

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
