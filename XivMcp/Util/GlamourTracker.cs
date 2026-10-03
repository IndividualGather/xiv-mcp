using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace XivMcp.Util;

/// <summary>
/// The armoire and the glamour dresser are only sent by the server after they were opened (inn room / dresser / glamour plates).
/// This tracker snapshots them whenever they are loaded and persists them per character.
/// </summary>
internal sealed class GlamourTracker : IDisposable, ICache
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NewVisitGap = TimeSpan.FromSeconds(60);

    public const string ArmoireHint = "open the armoire in an inn room (the snapshot updates automatically).";
    public const string DresserHint = "open the glamour dresser in an inn room, or a glamour plate (the snapshot updates automatically).";

    private readonly string filePath;
    private readonly object sync = new();
    private readonly Dictionary<ulong, GlamourSnapshot> data;
    private DateTime nextPoll = DateTime.MinValue;
    private long version;

    public GlamourTracker()
    {
        filePath = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "glamour.json");
        data = Load(filePath);
        Svc.Framework.Update += OnUpdate;
    }

    public void Dispose() => Svc.Framework.Update -= OnUpdate;

    public string Id => "glamour";
    public string Title => "Armoire & glamour dresser";
    public string Description => "Items stored in the armoire and in the glamour dresser (with dyes), captured when they were last opened.";
    public long Version => Interlocked.Read(ref version);
    public event Action<ICache>? Updated;

    public IEnumerable<CacheEntryStatus> Entries()
    {
        foreach (var s in All())
        {
            yield return new CacheEntryStatus(s.Character, "armoire", s.ArmoireCapturedUtc, ArmoireHint);
            yield return new CacheEntryStatus(s.Character, "glamour dresser", s.DresserCapturedUtc, DresserHint);
        }
    }

    public object Read() => All();

    public List<GlamourSnapshot> All()
    {
        lock (sync) return data.Values.OrderBy(s => s.Character).ToList();
    }

    public GlamourSnapshot? Get(ulong contentId)
    {
        lock (sync) return data.GetValueOrDefault(contentId);
    }

    /// <summary>True while the live game data is loaded (results are current, not a snapshot).</summary>
    public static unsafe bool ArmoireLive => UIState.Instance()->Cabinet.IsCabinetLoaded();
    public static unsafe bool DresserLive => MirageManager.Instance() != null && MirageManager.Instance()->PrismBoxLoaded;

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow + PollInterval;
        try { Poll(); }
        catch (Exception ex) { Svc.Log.Warning(ex, "Glamour snapshot failed"); }
    }

    private unsafe void Poll()
    {
        if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded) return;
        var armoireLive = ArmoireLive;
        var dresserLive = DresserLive;
        if (!armoireLive && !dresserLive) return;

        var contentId = Svc.PlayerState.ContentId;
        GlamourSnapshot current;
        lock (sync) current = data.GetValueOrDefault(contentId) ?? new GlamourSnapshot { ContentId = contentId };
        var now = DateTime.UtcNow;
        var next = current with { Character = Svc.PlayerState.CharacterName };
        var refreshed = false;

        if (armoireLive)
        {
            var cabinet = &UIState.Instance()->Cabinet;
            var ids = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>()
                .Where(r => r.RowId != 0 && r.Item.RowId != 0 && cabinet->IsItemInCabinet(r.RowId))
                .Select(r => r.RowId).ToList();
            refreshed |= !ids.SequenceEqual(current.ArmoireCabinetIds) || current.ArmoireCapturedUtc is not { } t || now - t > NewVisitGap;
            next = next with { ArmoireCabinetIds = ids, ArmoireCapturedUtc = now };
        }

        if (dresserLive)
        {
            var mm = MirageManager.Instance();
            var items = new List<DresserItem>();
            for (var i = 0; i < mm->PrismBoxItemIds.Length; i++)
                if (mm->PrismBoxItemIds[i] != 0)
                    items.Add(new DresserItem(i, mm->PrismBoxItemIds[i], mm->PrismBoxStain0Ids[i], mm->PrismBoxStain1Ids[i]));
            refreshed |= !items.SequenceEqual(current.DresserItems) || current.DresserCapturedUtc is not { } t || now - t > NewVisitGap;
            next = next with { DresserItems = items, DresserCapturedUtc = now, DresserCapacity = mm->PrismBoxItemIds.Length };
        }

        lock (sync) data[contentId] = next;
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

    private static Dictionary<ulong, GlamourSnapshot> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<GlamourSnapshot>>(File.ReadAllText(path)) is { } list)
                return list.ToDictionary(s => s.ContentId);
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "Could not read glamour snapshots"); }
        return [];
    }
}

public sealed record GlamourSnapshot
{
    public ulong ContentId { get; init; }
    public string Character { get; init; } = "";
    public DateTime? ArmoireCapturedUtc { get; init; }
    public List<uint> ArmoireCabinetIds { get; init; } = [];
    public DateTime? DresserCapturedUtc { get; init; }
    public int DresserCapacity { get; init; }
    public List<DresserItem> DresserItems { get; init; } = [];
}

/// <summary>A glamour dresser slot. ItemId may carry the HQ offset (+1,000,000).</summary>
public sealed record DresserItem(int Slot, uint ItemId, byte Stain0, byte Stain1);
