using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XivMcp.Util;

/// <summary>
/// Retainer data is only available at a summoning bell (the list) and while a retainer is open (its inventory).
/// This tracker snapshots both whenever they are readable and persists them per character.
/// </summary>
internal sealed class RetainerTracker : IDisposable, ICache
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NewVisitGap = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true };

    public const string ListRefreshHint = "open the retainer list at a summoning bell.";
    public static string InventoryRefreshHint(string retainer) => $"open retainer {retainer} at a summoning bell (or use open_retainer while at a bell).";

    private readonly string filePath;
    private readonly object sync = new();
    private readonly Dictionary<ulong, CharacterRetainers> data;
    private DateTime nextPoll = DateTime.MinValue;
    private readonly Dictionary<ulong, string> lastContent = [];
    private long version;

    public RetainerTracker()
    {
        filePath = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "retainers.json");
        data = Load(filePath);
        Svc.Framework.Update += OnUpdate;
    }

    public void Dispose() => Svc.Framework.Update -= OnUpdate;

    // ICache
    public string Id => "retainers";
    public string Title => "Retainers & retainer inventories";
    public string Description => "Retainer list (ventures, gil, levels) and every retainer's inventory, equipment, crystals and market listings, captured at the summoning bell.";
    public long Version => Interlocked.Read(ref version);
    public event Action<ICache>? Updated;

    public IEnumerable<CacheEntryStatus> Entries()
    {
        foreach (var c in All())
        {
            yield return new CacheEntryStatus(c.Label, "retainer list", c.ListCapturedUtc, ListRefreshHint);
            foreach (var r in c.Retainers)
                yield return new CacheEntryStatus(c.Label, $"inventory of {r.Name}", r.InventoryCapturedUtc, InventoryRefreshHint(r.Name));
        }
    }

    public object Read() => All();

    public List<CharacterRetainers> All()
    {
        lock (sync) return data.Values.OrderBy(c => c.Character).ToList();
    }

    public CharacterRetainers? Get(ulong contentId)
    {
        lock (sync) return data.GetValueOrDefault(contentId);
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow + PollInterval;
        try { Poll(); }
        catch (Exception ex) { Svc.Log.Warning(ex, "Retainer snapshot failed"); }
    }

    private unsafe void Poll()
    {
        if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded) return;
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return;
        // The game keeps retainer data in memory after leaving the bell; it is only refreshed by the server while at a bell.
        // (The "OccupiedSummoningBell" condition is also set at the company chest, so check the retainer windows themselves.)
        if (!RetainerUi.RetainerListOpen && !RetainerUi.InventoryOpen) return;

        var contentId = Svc.PlayerState.ContentId;
        CharacterRetainers current;
        lock (sync) current = data.GetValueOrDefault(contentId) ?? new CharacterRetainers { ContentId = contentId };
        var previousListTime = current.ListCapturedUtc;
        var now = DateTime.UtcNow;

        // Retainer list
        var retainers = new List<RetainerSnapshot>();
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0) continue;
            var old = current.Retainers.FirstOrDefault(x => x.RetainerId == r->RetainerId);
            retainers.Add((old ?? new RetainerSnapshot()) with
            {
                RetainerId = r->RetainerId,
                Name = r->NameString,
                SortIndex = (int)i,
                ClassJob = r->ClassJob,
                Level = r->Level,
                Gil = r->Gil,
                ItemCount = r->ItemCount,
                MarketItemCount = r->MarketItemCount,
                MarketExpire = r->MarketExpire,
                Town = r->Town.ToString(),
                VentureId = r->VentureId,
                VentureComplete = r->VentureComplete,
                Available = r->Available,
            });
        }
        if (retainers.Count == 0) return;
        var visitStarted = now - previousListTime > NewVisitGap;

        // Inventory of the open retainer
        var active = rm->GetActiveRetainer();
        var inventoryRefreshed = false;
        if (active != null && RetainerUi.InventoryOpen && RetainerUi.RetainerInventoryLoaded)
        {
            var index = retainers.FindIndex(x => x.RetainerId == active->RetainerId);
            if (index >= 0)
            {
                var prices = InventoryManager.Instance()->RetainerMarketPrices.ToArray();
                var snap = retainers[index];
                var openedNow = snap.InventoryCapturedUtc is null || now - snap.InventoryCapturedUtc > NewVisitGap;
                var updated = snap with
                {
                    Items = Read(GameInventoryType.RetainerPage1, GameInventoryType.RetainerPage2, GameInventoryType.RetainerPage3, GameInventoryType.RetainerPage4,
                                 GameInventoryType.RetainerPage5, GameInventoryType.RetainerPage6, GameInventoryType.RetainerPage7),
                    Equipped = Read(GameInventoryType.RetainerEquippedItems),
                    Crystals = Read(GameInventoryType.RetainerCrystals),
                    Market = Read(GameInventoryType.RetainerMarket).Select(m => m with { Price = m.Slot < prices.Length ? prices[m.Slot] : null }).ToList(),
                    InventoryCapturedUtc = now,
                };
                inventoryRefreshed = openedNow || Serialize(updated with { InventoryCapturedUtc = null }) != Serialize(snap with { InventoryCapturedUtc = null });
                retainers[index] = updated;
            }
        }

        var next = current with
        {
            Character = Svc.PlayerState.CharacterName,
            World = Excel.Name(Svc.PlayerState.HomeWorld),
            ListCapturedUtc = now,
            Retainers = retainers,
        };
        lock (sync) data[contentId] = next;

        var content = Serialize(next with { ListCapturedUtc = default, Retainers = next.Retainers.Select(r => r with { InventoryCapturedUtc = null }).ToList() });
        var listChanged = !lastContent.TryGetValue(contentId, out var last) || last != content;
        lastContent[contentId] = content;
        if (!visitStarted && !listChanged && !inventoryRefreshed) return;

        Save();
        Interlocked.Increment(ref version);
        Updated?.Invoke(this);
    }

    private static List<RetainerItem> Read(params GameInventoryType[] containers)
    {
        var list = new List<RetainerItem>();
        foreach (var c in containers)
            foreach (var item in Svc.Inventory.GetInventoryItems(c))
                if (!item.IsEmpty)
                    list.Add(new RetainerItem(c.ToString(), (int)item.InventorySlot, item.BaseItemId, item.IsHq, item.Quantity, item.IsCollectable, null));
        return list;
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o);

    private void Save()
    {
        string json;
        lock (sync) json = JsonSerializer.Serialize(data.Values.ToList(), FileOptions);
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, filePath, overwrite: true);
    }

    private static Dictionary<ulong, CharacterRetainers> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<CharacterRetainers>>(File.ReadAllText(path)) is { } list)
                return list.ToDictionary(c => c.ContentId);
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "Could not read retainer snapshots"); }
        return [];
    }
}

public sealed record CharacterRetainers
{
    public ulong ContentId { get; init; }
    public string Character { get; init; } = "";
    public string? World { get; init; }
    public DateTime ListCapturedUtc { get; init; }
    public List<RetainerSnapshot> Retainers { get; init; } = [];
    public string Label => World is null ? Character : $"{Character} @ {World}";
}

public sealed record RetainerSnapshot
{
    public ulong RetainerId { get; init; }
    public string Name { get; init; } = "";
    public int SortIndex { get; init; }
    public byte ClassJob { get; init; }
    public byte Level { get; init; }
    public uint Gil { get; init; }
    public byte ItemCount { get; init; }
    public byte MarketItemCount { get; init; }
    public uint MarketExpire { get; init; }
    public string Town { get; init; } = "";
    public ushort VentureId { get; init; }
    public uint VentureComplete { get; init; }
    public bool Available { get; init; }
    public DateTime? InventoryCapturedUtc { get; init; }
    public List<RetainerItem> Items { get; init; } = [];
    public List<RetainerItem> Equipped { get; init; } = [];
    public List<RetainerItem> Crystals { get; init; } = [];
    public List<RetainerItem> Market { get; init; } = [];
}

public sealed record RetainerItem(string Container, int Slot, uint ItemId, bool Hq, int Quantity, bool Collectable, ulong? Price);
