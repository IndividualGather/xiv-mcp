using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XivMcp.Caching;

/// <summary>A cached value and when it was fetched (UTC).</summary>
public readonly record struct Fetched<T>(T Value, DateTime FetchedUtc);

/// <summary>
/// Keeps values fetched online for <paramref name="lifetime"/>, so tools called one after another share one download. Calls that
/// arrive while a value is being fetched wait for that fetch instead of starting another. A failed fetch is not kept: the next call
/// tries again. A caller that stops waiting (its token is cancelled) does not stop the fetch, which the other callers may still wait
/// for; the fetch itself has to give up on its own (a timeout of its own).
/// </summary>
public sealed class TimedCache<TKey, TValue>(TimeSpan lifetime, Func<DateTime>? clock = null) where TKey : notnull
{
    private readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<TKey, Lazy<Task<Fetched<TValue>>>> entries = [];
    private readonly Lock sync = new();

    /// <summary>The value for <paramref name="key"/>: the cached one while it is younger than the lifetime, otherwise from <paramref name="fetch"/>.</summary>
    public Task<Fetched<TValue>> Get(TKey key, Func<Task<TValue>> fetch, CancellationToken ct)
    {
        Lazy<Task<Fetched<TValue>>> entry;
        lock (sync)
        {
            if (!entries.TryGetValue(key, out entry!) || Expired(entry))
                entries[key] = entry = new(() => Fetch(fetch));
        }
        // The fetch starts outside the lock; Lazy makes sure only one caller starts it.
        return entry.Value.WaitAsync(ct);
    }

    private bool Expired(Lazy<Task<Fetched<TValue>>> entry) =>
        entry.IsValueCreated && entry.Value.IsCompleted
        && (!entry.Value.IsCompletedSuccessfully || now() - entry.Value.Result.FetchedUtc >= lifetime);

    private async Task<Fetched<TValue>> Fetch(Func<Task<TValue>> fetch) => new(await fetch().ConfigureAwait(false), now());
}
