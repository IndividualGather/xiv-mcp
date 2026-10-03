using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Item sources from FFXIV Teamcraft's public data extract (the same data the Teamcraft website shows under "obtained from"):
/// vendors with positions, currency exchanges, drops, ventures, gathering nodes, desynthesis, voyages, quests, ...
/// The ~30 MB file is downloaded once into the plugin's config folder and refreshed when Teamcraft publishes a new one.
/// Only an index (item id → byte range) is kept in memory; an item's entry is parsed when asked for.
/// </summary>
internal static class Teamcraft
{
    private const string BaseUrl = "https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/";
    private const string ExtractsUrl = BaseUrl + "extracts/extracts.json";
    private const string HashUrl = BaseUrl + "extracts-hash.ts";
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    /// <summary>Teamcraft's DataType enum (libs/types/src/lib/list/data-type.ts).</summary>
    public enum SourceType
    {
        CraftedBy = 1, TradeSources = 2, Vendors = 3, ReducedFrom = 4, Desynths = 5, Instances = 6, GatheredBy = 7, Gardening = 8,
        Voyages = 9, Drops = 10, Alarms = 11, Masterbooks = 12, Treasures = 13, Fates = 14, Ventures = 15, TripleTriadDuels = 16,
        TripleTriadPack = 17, Quests = 18, Achievements = 19, Requirements = 20, Mogstation = 21, IslandPasture = 22, IslandCrop = 23,
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static byte[]? data;
    private static Dictionary<uint, (int Start, int Length)>? index;
    private static string? loadedHash;
    private static DateTime lastUpdateCheck = DateTime.MinValue;

    private static string Folder => Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "teamcraft");
    private static string DataFile => Path.Combine(Folder, "extracts.json");
    private static string HashFile => Path.Combine(Folder, "extracts.hash");

    public static HttpClient Http { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XivMcp/0.3 (Dalamud plugin)");
        return c;
    }

    public static object Status() => new
    {
        loaded = index is not null,
        items = index?.Count,
        version = loadedHash,
        downloaded = File.Exists(DataFile) ? File.GetLastWriteTimeUtc(DataFile) : (DateTime?)null,
    };

    /// <summary>The raw Teamcraft "sources" array of an item, or null if Teamcraft lists none.</summary>
    public static async Task<JsonArray?> Sources(uint itemId, CancellationToken ct)
    {
        await Ensure(ct).ConfigureAwait(false);
        if (!index!.TryGetValue(itemId, out var range)) return null;
        var node = JsonNode.Parse(data.AsSpan(range.Start, range.Length));
        return node?["sources"] as JsonArray;
    }

    /// <summary>Loads the extract (downloading or updating it if needed).</summary>
    private static async Task Ensure(CancellationToken ct)
    {
        if (index is not null && DateTime.UtcNow - lastUpdateCheck < UpdateCheckInterval) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (index is not null && DateTime.UtcNow - lastUpdateCheck < UpdateCheckInterval) return;
            Directory.CreateDirectory(Folder);
            var localHash = File.Exists(HashFile) ? (await File.ReadAllTextAsync(HashFile, ct).ConfigureAwait(false)).Trim() : null;

            string? remoteHash = null;
            try { remoteHash = await RemoteHash(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException && File.Exists(DataFile))
            {
                Svc.Log.Warning($"[MCP] Teamcraft update check failed, using the local copy: {ex.Message}");
            }
            lastUpdateCheck = DateTime.UtcNow;

            if (!File.Exists(DataFile) || (remoteHash is not null && remoteHash != localHash))
            {
                Svc.Log.Information($"[MCP] Downloading Teamcraft item data ({remoteHash ?? "?"})");
                var tmp = DataFile + ".tmp";
                await using (var http = await Http.GetStreamAsync(ExtractsUrl, ct).ConfigureAwait(false))
                await using (var file = File.Create(tmp))
                    await http.CopyToAsync(file, ct).ConfigureAwait(false);
                File.Move(tmp, DataFile, true);
                if (remoteHash is not null) await File.WriteAllTextAsync(HashFile, remoteHash, ct).ConfigureAwait(false);
                localHash = remoteHash;
                index = null;
            }

            if (index is null || loadedHash != localHash)
            {
                var bytes = await File.ReadAllBytesAsync(DataFile, ct).ConfigureAwait(false);
                index = BuildIndex(bytes);
                data = bytes;
                loadedHash = localHash;
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ToolException($"Could not download Teamcraft's item data: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> RemoteHash(CancellationToken ct)
    {
        var text = await Http.GetStringAsync(HashUrl, ct).ConfigureAwait(false);
        var m = Regex.Match(text, "[0-9a-f]{20,}");
        return m.Success ? m.Value : throw new InvalidDataException("Unexpected extracts-hash format.");
    }

    /// <summary>Scans the top-level object once and remembers where each item's value starts and ends.</summary>
    private static Dictionary<uint, (int, int)> BuildIndex(byte[] bytes)
    {
        var result = new Dictionary<uint, (int, int)>(50000);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 256 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Teamcraft data is not a JSON object.");
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString();
            reader.Read();
            var start = (int)reader.TokenStartIndex;
            reader.Skip();
            var end = (int)reader.BytesConsumed;
            if (uint.TryParse(key, out var id)) result[id] = (start, end - start);
        }
        return result;
    }
}
