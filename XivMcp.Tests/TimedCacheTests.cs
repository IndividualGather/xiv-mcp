using XivMcp.Caching;

namespace XivMcp.Tests;

/// <summary>Values fetched online are kept for a while and shared by the calls that need them.</summary>
public class TimedCacheTests
{
    private DateTime now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
    private readonly TimedCache<string, int> cache;
    private int fetches;

    public TimedCacheTests() => cache = new(TimeSpan.FromMinutes(5), () => now);

    private Func<Task<int>> Counting() => () => Task.FromResult(++fetches);

    [Fact]
    public async Task A_value_is_kept_for_its_lifetime()
    {
        var first = await cache.Get("report", Counting(), default);
        now += TimeSpan.FromMinutes(4);
        var second = await cache.Get("report", Counting(), default);
        Assert.Equal(1, fetches);
        Assert.Equal(first, second);
        Assert.Equal(new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc), second.FetchedUtc);
    }

    [Fact]
    public async Task An_old_value_is_fetched_again()
    {
        await cache.Get("report", Counting(), default);
        now += TimeSpan.FromMinutes(5);
        var again = await cache.Get("report", Counting(), default);
        Assert.Equal(2, again.Value);
        Assert.Equal(now, again.FetchedUtc);
    }

    [Fact]
    public async Task Keys_are_kept_apart()
    {
        await cache.Get("a", Counting(), default);
        await cache.Get("b", Counting(), default);
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task Calls_during_a_fetch_wait_for_it_instead_of_fetching_again()
    {
        var download = new TaskCompletionSource<int>();
        Func<Task<int>> slow = () => { fetches++; return download.Task; };
        var first = cache.Get("report", slow, default);
        var second = cache.Get("report", slow, default);
        Assert.False(second.IsCompleted);
        download.SetResult(7);
        Assert.Equal(7, (await first).Value);
        Assert.Equal(7, (await second).Value);
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task A_failed_fetch_is_not_kept()
    {
        Func<Task<int>> down = async () => { await Task.Yield(); throw new InvalidOperationException("down"); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.Get("report", down, default));
        var next = await cache.Get("report", Counting(), default);
        Assert.Equal(1, next.Value);
    }

    [Fact]
    public async Task A_fetch_that_throws_before_it_awaits_is_not_kept_either()
    {
        Func<Task<int>> broken = () => throw new InvalidOperationException("bad url");
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.Get("report", broken, default));
        Assert.Equal(1, (await cache.Get("report", Counting(), default)).Value);
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_stop_the_fetch_for_the_others()
    {
        var download = new TaskCompletionSource<int>();
        Func<Task<int>> slow = () => { fetches++; return download.Task; };
        using var givesUp = new CancellationTokenSource();
        var impatient = cache.Get("report", slow, givesUp.Token);
        var patient = cache.Get("report", slow, default);
        givesUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        download.SetResult(3);
        Assert.Equal(3, (await patient).Value);
        Assert.Equal(3, (await cache.Get("report", Counting(), default)).Value);
        Assert.Equal(1, fetches);
    }
}
