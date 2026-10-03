using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XivMcp.Util;

/// <summary>
/// Achievement and title unlocks are only available after the game loaded them (Achievements window / title list opened once per
/// session — by the player or any plugin). This tracker snapshots them whenever they are loaded, so they can be answered later.
/// </summary>
internal sealed class ProgressTracker : IDisposable, ICache
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NewVisitGap = TimeSpan.FromMinutes(5);

    public const string AchievementHint = "use load_game_data with data=\"achievements\" (or open the Achievements window once).";
    public const string TitleHint = "use load_game_data with data=\"titles\" (or open_window \"Titles\").";

    public static ProgressTracker? Instance { get; private set; }

    private readonly string filePath;
    private readonly object sync = new();
    private readonly Dictionary<ulong, ProgressSnapshot> data;
    private DateTime nextPoll = DateTime.MinValue;
    private long version;

    public ProgressTracker()
    {
        filePath = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "progress.json");
        data = Load(filePath);
        Svc.Framework.Update += OnUpdate;
        Instance = this;
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
        if (Instance == this) Instance = null;
    }

    public string Id => "progress";
    public string Title => "Achievements & titles";
    public string Description => "Completed achievements and unlocked titles, captured whenever the game has them loaded.";
    public long Version => Interlocked.Read(ref version);
    public event Action<ICache>? Updated;

    public IEnumerable<CacheEntryStatus> Entries()
    {
        foreach (var s in All())
        {
            var mine = Svc.PlayerState.IsLoaded && Svc.PlayerState.ContentId == s.ContentId;
            yield return new CacheEntryStatus(s.Character, "achievements", s.AchievementsCapturedUtc, AchievementHint, mine && Svc.Unlocks.IsAchievementListLoaded);
            yield return new CacheEntryStatus(s.Character, "titles", s.TitlesCapturedUtc, TitleHint, mine && Svc.Unlocks.IsTitleListLoaded);
        }
    }

    public object Read() => All().Select(s => new
    {
        s.Character,
        s.AchievementsCapturedUtc,
        completedAchievements = s.Achievements.Count,
        s.TitlesCapturedUtc,
        unlockedTitles = s.Titles.Count,
    }).ToList();

    public List<ProgressSnapshot> All()
    {
        lock (sync) return data.Values.OrderBy(s => s.Character).ToList();
    }

    public ProgressSnapshot? Current()
    {
        if (!Svc.PlayerState.IsLoaded) return null;
        lock (sync) return data.GetValueOrDefault(Svc.PlayerState.ContentId);
    }

    /// <summary>Cached unlocked row ids for "achievement" or "title", with capture time; null if nothing was captured.</summary>
    public (HashSet<uint> Ids, DateTime Captured, string Hint)? Cached(string category)
    {
        var s = Current();
        return category switch
        {
            "achievement" when s?.AchievementsCapturedUtc is { } t => (s.Achievements.ToHashSet(), t, AchievementHint),
            "title" when s?.TitlesCapturedUtc is { } t => (s.Titles.ToHashSet(), t, TitleHint),
            _ => null,
        };
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow + PollInterval;
        try { Poll(); }
        catch (Exception ex) { Svc.Log.Warning(ex, "Progress snapshot failed"); }
    }

    private void Poll()
    {
        if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded) return;
        var achievementsLoaded = Svc.Unlocks.IsAchievementListLoaded;
        var titlesLoaded = Svc.Unlocks.IsTitleListLoaded;
        if (!achievementsLoaded && !titlesLoaded) return;

        var contentId = Svc.PlayerState.ContentId;
        ProgressSnapshot current;
        lock (sync) current = data.GetValueOrDefault(contentId) ?? new ProgressSnapshot { ContentId = contentId };
        var now = DateTime.UtcNow;
        var next = current with { Character = Svc.PlayerState.CharacterName };
        var refreshed = false;

        if (achievementsLoaded)
        {
            var done = Svc.Data.GetExcelSheet<Achievement>().Where(a => a.RowId != 0 && !a.Name.IsEmpty && Svc.Unlocks.IsAchievementComplete(a)).Select(a => a.RowId).ToList();
            refreshed |= !done.SequenceEqual(current.Achievements) || current.AchievementsCapturedUtc is not { } t || now - t > NewVisitGap;
            next = next with { Achievements = done, AchievementsCapturedUtc = now };
        }
        if (titlesLoaded)
        {
            var titles = Svc.Data.GetExcelSheet<Title>().Where(x => x.RowId != 0 && Svc.Unlocks.IsTitleUnlocked(x)).Select(x => x.RowId).ToList();
            refreshed |= !titles.SequenceEqual(current.Titles) || current.TitlesCapturedUtc is not { } t || now - t > NewVisitGap;
            next = next with { Titles = titles, TitlesCapturedUtc = now };
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

    private static Dictionary<ulong, ProgressSnapshot> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<ProgressSnapshot>>(File.ReadAllText(path)) is { } list)
                return list.ToDictionary(s => s.ContentId);
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "Could not read progress snapshots"); }
        return [];
    }
}

public sealed record ProgressSnapshot
{
    public ulong ContentId { get; init; }
    public string Character { get; init; } = "";
    public DateTime? AchievementsCapturedUtc { get; init; }
    public List<uint> Achievements { get; init; } = [];
    public DateTime? TitlesCapturedUtc { get; init; }
    public List<uint> Titles { get; init; } = [];
}
