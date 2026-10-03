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
/// Storages the game only sends after they were opened: the chocobo saddlebag (once per session) and the free company chest.
/// Snapshotted whenever loaded (by the player or a plugin such as FCCH), persisted per character.
/// </summary>
internal sealed class StorageTracker : IDisposable, ICache
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NewVisitGap = TimeSpan.FromMinutes(2);

    public static readonly Dictionary<string, (string Title, GameInventoryType[] Containers, string Hint)> Groups = new()
    {
        ["saddlebag"] = ("Chocobo saddlebag",
            [GameInventoryType.SaddleBag1, GameInventoryType.SaddleBag2, GameInventoryType.PremiumSaddleBag1, GameInventoryType.PremiumSaddleBag2],
            "open the chocobo saddlebag once (or use open_window \"Chocobo Saddlebag\")."),
        ["fc_chest"] = ("Free company chest",
            [GameInventoryType.FreeCompanyPage1, GameInventoryType.FreeCompanyPage2, GameInventoryType.FreeCompanyPage3, GameInventoryType.FreeCompanyPage4,
             GameInventoryType.FreeCompanyPage5, GameInventoryType.FreeCompanyCrystals],
            "open the company chest (or use interact_with_object \"Company Chest\")."),
    };

    public static StorageTracker? Instance { get; private set; }

    private readonly string filePath;
    private readonly object sync = new();
    private readonly Dictionary<ulong, StorageSnapshot> data;
    private DateTime nextPoll = DateTime.MinValue;
    private long version;

    public StorageTracker()
    {
        filePath = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "storage.json");
        data = Load(filePath);
        Svc.Framework.Update += OnUpdate;
        Instance = this;
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
        if (Instance == this) Instance = null;
    }

    public string Id => "storage";
    public string Title => "Saddlebag & FC chest";
    public string Description => "Contents of the chocobo saddlebag and the free company chest, captured whenever the game has them loaded.";
    public long Version => Interlocked.Read(ref version);
    public event Action<ICache>? Updated;

    public IEnumerable<CacheEntryStatus> Entries()
    {
        foreach (var s in All())
        {
            var mine = Svc.PlayerState.IsLoaded && Svc.PlayerState.ContentId == s.ContentId;
            foreach (var (key, group) in Groups)
                yield return new CacheEntryStatus(s.Character, group.Title, s.Groups.GetValueOrDefault(key)?.CapturedUtc, group.Hint, mine && IsLoaded(key));
        }
    }

    public object Read() => All();

    public List<StorageSnapshot> All()
    {
        lock (sync) return data.Values.OrderBy(s => s.Character).ToList();
    }

    public StoredGroup? Get(string group)
    {
        if (!Svc.PlayerState.IsLoaded) return null;
        lock (sync) return data.GetValueOrDefault(Svc.PlayerState.ContentId)?.Groups.GetValueOrDefault(group);
    }

    /// <summary>Whether the game currently holds the group's data (its first container is loaded).</summary>
    public static unsafe bool IsLoaded(string group)
    {
        var c = InventoryManager.Instance()->GetInventoryContainer((InventoryType)Groups[group].Containers[0]);
        return c != null && c->IsLoaded;
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow + PollInterval;
        try { Poll(); }
        catch (Exception ex) { Svc.Log.Warning(ex, "Storage snapshot failed"); }
    }

    private unsafe void Poll()
    {
        if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded) return;
        var contentId = Svc.PlayerState.ContentId;
        StorageSnapshot current;
        lock (sync) current = data.GetValueOrDefault(contentId) ?? new StorageSnapshot { ContentId = contentId };
        var groups = new Dictionary<string, StoredGroup>(current.Groups);
        var now = DateTime.UtcNow;
        var refreshed = false;

        foreach (var (key, group) in Groups)
        {
            if (!IsLoaded(key)) continue;
            var items = new List<StoredItem>();
            foreach (var container in group.Containers)
                foreach (var item in Svc.Inventory.GetInventoryItems(container))
                    if (!item.IsEmpty)
                        items.Add(new StoredItem(container.ToString(), (int)item.InventorySlot, item.BaseItemId, item.IsHq, item.Quantity));
            uint? gil = key == "fc_chest" ? InventoryManager.Instance()->GetFreeCompanyGil() : null;
            var previous = groups.GetValueOrDefault(key);
            refreshed |= previous is null || !previous.Items.SequenceEqual(items) || previous.Gil != gil || now - previous.CapturedUtc > NewVisitGap;
            groups[key] = new StoredGroup(now, items, gil);
        }
        if (groups.Count == current.Groups.Count && !refreshed && groups.All(g => current.Groups.TryGetValue(g.Key, out var p) && p.CapturedUtc == g.Value.CapturedUtc))
            return;

        lock (sync) data[contentId] = current with { Character = Svc.PlayerState.CharacterName, Groups = groups };
        if (!refreshed) return;
        Save();
        Interlocked.Increment(ref version);
        Updated?.Invoke(this);
    }

    private void Save()
    {
        string json;
        lock (sync) json = JsonSerializer.Serialize(data.Values.ToList());
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, filePath, overwrite: true);
    }

    private static Dictionary<ulong, StorageSnapshot> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<StorageSnapshot>>(File.ReadAllText(path)) is { } list)
                return list.ToDictionary(s => s.ContentId);
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "Could not read storage snapshots"); }
        return [];
    }
}

public sealed record StorageSnapshot
{
    public ulong ContentId { get; init; }
    public string Character { get; init; } = "";
    public Dictionary<string, StoredGroup> Groups { get; init; } = [];
}

public sealed record StoredGroup(DateTime CapturedUtc, List<StoredItem> Items, uint? Gil);

public sealed record StoredItem(string Container, int Slot, uint ItemId, bool Hq, int Quantity);
