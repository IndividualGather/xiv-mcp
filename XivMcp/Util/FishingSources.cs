using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Fishing;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// The fishing data XIV MCP looks up online (module "Online lookups"):
/// <list type="bullet">
/// <item>the community fish tracker's data (Carbuncle Plushy's FFX|V Fish Tracker, MIT): time windows, weather, best catch path,
/// Fisher's Intuition, folklore, hooksets and tugs of the fish worth tracking;</item>
/// <item>FFXIV Teamcraft's fishing data (MIT): the spots and baits of every fish, and what bites at each spot;</item>
/// <item>bite times players reported to Teamcraft, per spot and bait.</item>
/// </list>
/// The files are kept in the plugin's config folder and downloaded again once a day; if a download fails, the last copy is used.
/// </summary>
internal static class FishingSources
{
    private const string TrackerUrl = "https://raw.githubusercontent.com/icykoneko/ff14-fish-tracker-app/master/js/app/data.js";
    private const string TeamcraftUrl = "https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json/";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    internal sealed record Data(
        IReadOnlyDictionary<uint, TrackerFish> Tracker, IReadOnlyDictionary<uint, string> Folklore,
        IReadOnlyDictionary<uint, IReadOnlyList<FishSource>> Sources, IReadOnlyDictionary<uint, FishingSpotInfo> Spots);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Data? data;
    private static DateTime loadedAt = DateTime.MinValue;
    private static readonly ConcurrentDictionary<(uint Spot, uint Bait), (DateTime At, IReadOnlyDictionary<uint, BiteWindow> Windows)> Bites = new();

    private static string Folder => Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "fishing");

    /// <summary>The data if it was loaded before, without going online.</summary>
    public static Data? Cached => data;

    /// <summary>The fish data, downloading what is missing or older than a day.</summary>
    public static async Task<Data> Get(CancellationToken ct)
    {
        if (data is not null && DateTime.UtcNow - loadedAt < MaxAge) return data;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (data is not null && DateTime.UtcNow - loadedAt < MaxAge) return data;
            Directory.CreateDirectory(Folder);
            var tracker = await File(TrackerUrl, "fish-tracker.js", ct).ConfigureAwait(false);
            var sources = await File(TeamcraftUrl + "fishing-sources.json", "fishing-sources.json", ct).ConfigureAwait(false);
            var spots = await File(TeamcraftUrl + "fishing-spots.json", "fishing-spots.json", ct).ConfigureAwait(false);
            data = await Task.Run(() => new Data(
                FishTrackerData.ParseFish(tracker), FishTrackerData.ParseFolklore(tracker),
                TeamcraftFishData.ParseSources(sources), TeamcraftFishData.ParseSpots(spots)), ct).ConfigureAwait(false);
            loadedAt = DateTime.UtcNow;
            return data;
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Reported bite times of every fish at a spot with a bait, or an empty set if Teamcraft does not answer. Kept for a day.
    /// </summary>
    public static async Task<IReadOnlyDictionary<uint, BiteWindow>> BiteTimes(uint spot, uint bait, CancellationToken ct)
    {
        if (Bites.TryGetValue((spot, bait), out var cached) && DateTime.UtcNow - cached.At < MaxAge) return cached.Windows;
        try
        {
            using var content = new StringContent(TeamcraftBiteTimes.Query(spot, bait), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Teamcraft.Http.PostAsync(TeamcraftBiteTimes.Endpoint, content, cts.Token).ConfigureAwait(false);
            var windows = TeamcraftBiteTimes.Parse(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            Bites[(spot, bait)] = (DateTime.UtcNow, windows);
            return windows;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Svc.Log.Warning($"[MCP] Teamcraft bite times for spot {spot}, bait {bait}: {ex.Message}");
            return cached.Windows ?? new Dictionary<uint, BiteWindow>();
        }
    }

    /// <summary>A file from the cache, downloaded again when older than a day; the old copy is used when the download fails.</summary>
    private static async Task<string> File(string url, string name, CancellationToken ct)
    {
        var path = Path.Combine(Folder, name);
        var info = new FileInfo(path);
        if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < MaxAge) return await System.IO.File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        try
        {
            var text = await Teamcraft.Http.GetStringAsync(url, ct).ConfigureAwait(false);
            await System.IO.File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
            return text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && info.Exists)
        {
            Svc.Log.Warning($"[MCP] Could not update {name}, using the copy from {info.LastWriteTimeUtc:u}: {ex.Message}");
            return await System.IO.File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ToolException($"Could not download the fishing data ({name}): {ex.Message}");
        }
    }
}
