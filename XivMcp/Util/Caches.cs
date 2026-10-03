using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XivMcp.Util;

/// <summary>A snapshot cache of data the game only provides in certain places (workshop, summoning bell, ...).</summary>
internal interface ICache
{
    /// <summary>Short id, also used in the resource URI xiv://cache/&lt;id&gt;.</summary>
    string Id { get; }
    string Title { get; }
    string Description { get; }

    /// <summary>Bumped whenever a new snapshot was captured (content changed or first capture after a while).</summary>
    long Version { get; }

    event Action<ICache>? Updated;

    /// <summary>Per-entry capture times (e.g. per retainer), for status reports.</summary>
    IEnumerable<CacheEntryStatus> Entries();

    /// <summary>The cached data as a JSON-friendly object (all characters).</summary>
    object Read();
}

/// <summary>One cache entry. Live = the game currently holds this data, so tools read it live and the age is irrelevant.</summary>
internal sealed record CacheEntryStatus(string Character, string? Entry, DateTime? CapturedUtc, string RefreshHint, bool Live = false);

internal static class CacheFreshness
{
    /// <summary>Snapshots older than this get a refresh suggestion.</summary>
    public static TimeSpan StaleAfter => TimeSpan.FromHours(Math.Clamp(Plugin.Instance?.Config.CacheStaleHours ?? 12, 1, 24 * 14));

    /// <summary>Standard "cache" block attached to every cached result.</summary>
    public static object Describe(DateTime? capturedUtc, bool live, string refreshHint) =>
        capturedUtc is { } t ? Describe(t, live, refreshHint) : new { notCaptured = true, stale = true, suggestion = $"Not captured yet. To capture it: {refreshHint}" };

    public static object Describe(DateTime capturedUtc, bool live, string refreshHint)
    {
        var age = DateTime.UtcNow - capturedUtc;
        var stale = !live && age > StaleAfter;
        return new
        {
            capturedAt = capturedUtc,
            ageMinutes = Math.Round(age.TotalMinutes, 1),
            age = FormatAge(age),
            live,
            stale,
            suggestion = live ? null : stale
                ? $"This data is {FormatAge(age)} old. To refresh: {refreshHint}"
                : $"Cached snapshot. To refresh: {refreshHint}",
        };
    }

    public static string FormatAge(TimeSpan age) => age.TotalMinutes < 1 ? "less than a minute"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours}h {age.Minutes:00}m"
        : $"{(int)age.TotalDays}d {age.Hours}h";
}

/// <summary>All caches, plus waiting for updates.</summary>
internal sealed class CacheRegistry
{
    private readonly Dictionary<string, ICache> caches = new(StringComparer.OrdinalIgnoreCase);

    public event Action<ICache>? Updated;

    public void Add(ICache cache)
    {
        caches[cache.Id] = cache;
        cache.Updated += c => Updated?.Invoke(c);
    }

    public IReadOnlyCollection<ICache> All => caches.Values;

    public static string Uri(ICache cache) => $"xiv://cache/{cache.Id}";

    public ICache Get(string idOrUri)
    {
        var id = idOrUri.StartsWith("xiv://cache/", StringComparison.OrdinalIgnoreCase) ? idOrUri["xiv://cache/".Length..] : idOrUri;
        return caches.TryGetValue(id.Trim('/'), out var c)
            ? c
            : throw new Mcp.ToolException($"Unknown cache '{idOrUri}'. Known: {string.Join(", ", caches.Keys)}");
    }

    /// <summary>Waits until the cache's version is greater than <paramref name="sinceVersion"/>. Returns false on timeout.</summary>
    public static async Task<bool> WaitForUpdate(ICache cache, long sinceVersion, TimeSpan timeout, CancellationToken ct)
    {
        if (cache.Version > sinceVersion) return true;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ICache c) { if (c.Version > sinceVersion) tcs.TrySetResult(); }
        cache.Updated += Handler;
        try
        {
            if (cache.Version > sinceVersion) return true;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
            {
                try { await tcs.Task.ConfigureAwait(false); return true; }
                catch (TaskCanceledException) { ct.ThrowIfCancellationRequested(); return false; }
            }
        }
        finally { cache.Updated -= Handler; }
    }

    public object Status() => caches.Values.Select(c => new
    {
        cache = c.Id,
        uri = Uri(c),
        title = c.Title,
        version = c.Version,
        entries = c.Entries().OrderBy(e => e.Character).ThenBy(e => e.Entry).Select(e => new
        {
            character = e.Character,
            entry = e.Entry,
            freshness = CacheFreshness.Describe(e.CapturedUtc, e.Live, e.RefreshHint),
        }).ToList(),
    }).ToList();
}
