using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace XivMcp.Util;

/// <summary>
/// The game only has submersible/airship data while the player is inside the FC workshop. This tracker snapshots it
/// whenever it is readable and persists the snapshots (per character) so they can be queried from anywhere later.
/// </summary>
internal sealed class WorkshopTracker : IDisposable, ICache
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true };

    private readonly string filePath;
    private readonly object sync = new();
    private Dictionary<ulong, WorkshopSnapshot> snapshots;
    private DateTime nextPoll = DateTime.MinValue;
    private string? lastSerialized;

    public WorkshopTracker()
    {
        filePath = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "workshop.json");
        snapshots = Load(filePath);
        Svc.Framework.Update += OnUpdate;
    }

    public void Dispose() => Svc.Framework.Update -= OnUpdate;

    /// <summary>True while the player is in the workshop and the data is live.</summary>
    public bool IsInWorkshop { get; private set; }

    public WorkshopSnapshot? Get(ulong contentId)
    {
        lock (sync) return snapshots.GetValueOrDefault(contentId);
    }

    public List<WorkshopSnapshot> All()
    {
        lock (sync) return snapshots.Values.OrderBy(s => s.Character).ToList();
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow + PollInterval;
        try { Poll(); }
        catch (Exception ex) { Svc.Log.Warning(ex, "Workshop snapshot failed"); }
    }

    private unsafe void Poll()
    {
        IsInWorkshop = false;
        if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded) return;
        var hm = HousingManager.Instance();
        if (hm == null || hm->WorkshopTerritory == null) return;
        IsInWorkshop = true;
        var ws = hm->WorkshopTerritory;

        var subs = new List<VesselSnapshot>();
        foreach (ref var s in ws->Submersible.Data)
        {
            if (s.RankId == 0 || string.IsNullOrEmpty(s.NameString)) continue;
            var loot = new List<LootSnapshot>();
            foreach (ref var g in s.GatheredData)
            {
                if (g.Point == 0) continue;
                loot.Add(new LootSnapshot
                {
                    Point = g.Point,
                    ExpGained = g.ExpGained,
                    ItemPrimary = g.ItemIdPrimary, CountPrimary = g.ItemCountPrimary, HqPrimary = g.ItemHQPrimary,
                    ItemAdditional = g.ItemIdAdditional, CountAdditional = g.ItemCountAdditional, HqAdditional = g.ItemHQAdditional,
                    DoubleDip = g.DoubleDip, FirstExploration = g.FirstExploration, UnlockedPoint = g.UnlockedPoint,
                });
            }
            subs.Add(new VesselSnapshot
            {
                Name = s.NameString,
                Rank = s.RankId,
                Exp = s.CurrentExp,
                NextLevelExp = s.NextLevelExp,
                Hull = s.HullId, Stern = s.SternId, Bow = s.BowId, Bridge = s.BridgeId,
                Base = [s.SurveillanceBase, s.RetrievalBase, s.SpeedBase, s.RangeBase, s.FavorBase],
                Bonus = [s.SurveillanceBonus, s.RetrievalBonus, s.SpeedBonus, s.RangeBonus, s.FavorBonus],
                RegisterTime = s.RegisterTime,
                ReturnTime = s.ReturnTime,
                Points = s.CurrentExplorationPoints.ToArray().Where(p => p != 0).ToArray(),
                Loot = loot,
            });
        }

        var airships = new List<VesselSnapshot>();
        foreach (ref var a in ws->Airship.Data)
        {
            if (a.RankId == 0 || string.IsNullOrEmpty(a.NameString)) continue;
            airships.Add(new VesselSnapshot
            {
                Name = a.NameString,
                Rank = a.RankId,
                Exp = a.CurrentExp,
                NextLevelExp = a.NextLevelExp,
                Hull = a.HullId, Stern = a.SternId, Bow = a.BowId, Bridge = a.BridgeId,
                Base = [a.Surveillance, a.Retrieval, a.Speed, a.Range, a.Favor],
                RegisterTime = a.RegisterTime,
                ReturnTime = a.ReturnTime,
            });
        }

        // Data can be empty for a moment after entering; never overwrite a good snapshot with nothing.
        if (subs.Count == 0 && airships.Count == 0) return;

        var unlocked = new List<byte>();
        var explored = new List<byte>();
        foreach (var row in Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.SubmarineExploration>())
        {
            if (row.RowId == 0 || row.RowId > byte.MaxValue || row.StartingPoint) continue;
            var point = (byte)row.RowId;
            if (HousingManager.IsSubmarineExplorationUnlocked(point)) unlocked.Add(point);
            if (HousingManager.IsSubmarineExplorationExplored(point)) explored.Add(point);
        }

        var snapshot = new WorkshopSnapshot
        {
            ContentId = Svc.PlayerState.ContentId,
            Character = Svc.PlayerState.CharacterName,
            World = Excel.Name(Svc.PlayerState.HomeWorld),
            CapturedUtc = DateTime.UtcNow,
            Submersibles = subs,
            Airships = airships,
            UnlockedSectors = unlocked,
            ExploredSectors = explored,
        };

        // A "refresh" is a content change, or the first capture of a new visit. Polls in between only move the timestamp.
        var comparable = JsonSerializer.Serialize(snapshot with { CapturedUtc = default });
        WorkshopSnapshot? previous;
        lock (sync)
        {
            previous = snapshots.GetValueOrDefault(snapshot.ContentId);
            snapshots[snapshot.ContentId] = snapshot;
        }
        var newVisit = previous is null || DateTime.UtcNow - previous.CapturedUtc > TimeSpan.FromSeconds(60);
        if (comparable == lastSerialized && !newVisit) return;
        lastSerialized = comparable;
        Save();
        Interlocked.Increment(ref version);
        Updated?.Invoke(this);
    }

    // ICache
    private long version;
    public string Id => "submersibles";
    public string Title => "FC submersibles & airships";
    public string Description => "Snapshot of the free company workshop (submersibles, airships, sectors), captured while inside the workshop.";
    public long Version => Interlocked.Read(ref version);
    public event Action<ICache>? Updated;
    public const string RefreshHint = "go to your free company's workshop and open the voyage control panel (submersible management); the snapshot updates automatically.";

    public IEnumerable<CacheEntryStatus> Entries() =>
        All().Select(s => new CacheEntryStatus($"{s.Character}{(s.World is null ? "" : " @ " + s.World)}", null, s.CapturedUtc, RefreshHint));

    public object Read() => All();

    private void Save()
    {
        string json;
        lock (sync) json = JsonSerializer.Serialize(snapshots.Values.ToList(), FileOptions);
        var tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, filePath, overwrite: true);
    }

    private static Dictionary<ulong, WorkshopSnapshot> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<WorkshopSnapshot>>(File.ReadAllText(path)) is { } list)
                return list.ToDictionary(s => s.ContentId);
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "Could not read workshop snapshots"); }
        return [];
    }
}

public sealed record WorkshopSnapshot
{
    public ulong ContentId { get; init; }
    public string Character { get; init; } = "";
    public string? World { get; init; }
    public DateTime CapturedUtc { get; init; }
    public List<VesselSnapshot> Submersibles { get; init; } = [];
    public List<VesselSnapshot> Airships { get; init; } = [];
    public List<byte> UnlockedSectors { get; init; } = [];
    public List<byte> ExploredSectors { get; init; } = [];
}

public sealed record VesselSnapshot
{
    public string Name { get; init; } = "";
    public byte Rank { get; init; }
    public uint Exp { get; init; }
    public uint NextLevelExp { get; init; }
    public ushort Hull { get; init; }
    public ushort Stern { get; init; }
    public ushort Bow { get; init; }
    public ushort Bridge { get; init; }
    /// <summary>Surveillance, Retrieval, Speed, Range, Favor.</summary>
    public ushort[] Base { get; init; } = [];
    public ushort[] Bonus { get; init; } = [];
    public uint RegisterTime { get; init; }
    public uint ReturnTime { get; init; }
    public byte[] Points { get; init; } = [];
    public List<LootSnapshot> Loot { get; init; } = [];
}

public sealed record LootSnapshot
{
    public byte Point { get; init; }
    public uint ExpGained { get; init; }
    public uint ItemPrimary { get; init; }
    public ushort CountPrimary { get; init; }
    public bool HqPrimary { get; init; }
    public uint ItemAdditional { get; init; }
    public ushort CountAdditional { get; init; }
    public bool HqAdditional { get; init; }
    public bool DoubleDip { get; init; }
    public bool FirstExploration { get; init; }
    public byte UnlockedPoint { get; init; }
}
