using XivMcp.Crafting;

namespace XivMcp.Tests;

public class GatherStallWatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(6);

    private static Dictionary<uint, int> Counts(int mogpom) => new() { [1] = 10, [2] = mogpom };
    private static GatherActivity Idle(float x = 0) => new(Busy: false, Waiting: false, X: x, Y: 0, Z: 0);

    [Fact]
    public void Idle_without_progress_for_the_window_is_a_stall()
    {
        var watch = new GatherStallWatch(Window, T0);
        Assert.False(watch.Observe(T0, Counts(0), Idle()));
        Assert.False(watch.Observe(T0.AddMinutes(5), Counts(0), Idle()));
        Assert.True(watch.Observe(T0.AddMinutes(6), Counts(0), Idle()));
        Assert.Equal(TimeSpan.FromMinutes(6), watch.IdleFor(T0.AddMinutes(6)));
    }

    [Fact]
    public void A_count_change_restarts_the_clock()
    {
        var watch = new GatherStallWatch(Window, T0);
        watch.Observe(T0, Counts(0), Idle());
        Assert.False(watch.Observe(T0.AddMinutes(5), Counts(1), Idle()));
        Assert.False(watch.Observe(T0.AddMinutes(10), Counts(1), Idle()));
        Assert.True(watch.Observe(T0.AddMinutes(11), Counts(1), Idle()));
    }

    [Fact]
    public void Gathering_fishing_or_travelling_is_not_idle()
    {
        var watch = new GatherStallWatch(Window, T0);
        watch.Observe(T0, Counts(0), Idle());
        Assert.False(watch.Observe(T0.AddMinutes(5), Counts(0), Idle() with { Busy = true }));
        Assert.False(watch.Observe(T0.AddMinutes(10), Counts(0), Idle()));
        Assert.True(watch.Observe(T0.AddMinutes(11), Counts(0), Idle()));
    }

    [Fact]
    public void Moving_is_not_idle_but_small_jitter_is()
    {
        var watch = new GatherStallWatch(Window, T0);
        watch.Observe(T0, Counts(0), Idle(0));
        Assert.False(watch.Observe(T0.AddMinutes(3), Counts(0), Idle(0.2f)));
        Assert.False(watch.Observe(T0.AddMinutes(5), Counts(0), Idle(20)));
        Assert.False(watch.Observe(T0.AddMinutes(10), Counts(0), Idle(20)));
        Assert.True(watch.Observe(T0.AddMinutes(11), Counts(0), Idle(20)));
    }

    [Fact]
    public void Waiting_for_a_timed_node_is_never_a_stall()
    {
        var watch = new GatherStallWatch(Window, T0);
        watch.Observe(T0, Counts(0), Idle());
        Assert.False(watch.Observe(T0.AddMinutes(30), Counts(0), Idle() with { Waiting = true }));
        Assert.False(watch.Observe(T0.AddMinutes(35), Counts(0), Idle()));
        Assert.True(watch.Observe(T0.AddMinutes(36), Counts(0), Idle()));
    }

    [Fact]
    public void Reset_restarts_the_clock_after_a_restart()
    {
        var watch = new GatherStallWatch(Window, T0);
        watch.Observe(T0, Counts(0), Idle());
        watch.Reset(T0.AddMinutes(5));
        Assert.False(watch.Observe(T0.AddMinutes(10), Counts(0), Idle()));
        Assert.True(watch.Observe(T0.AddMinutes(11), Counts(0), Idle()));
    }

    [Fact]
    public void A_zero_window_disables_stall_detection()
    {
        var watch = new GatherStallWatch(TimeSpan.Zero, T0);
        Assert.False(watch.Observe(T0.AddHours(3), Counts(0), Idle()));
    }

    [Theory]
    [InlineData(true, "Fishing...", true)]
    [InlineData(null, "Waiting for the node to spawn", true)]
    [InlineData(false, "waiting until 14:00 ET", true)]
    [InlineData(false, "Fishing...", false)]
    [InlineData(null, null, false)]
    public void Waiting_comes_from_the_ipc_or_the_status_text(bool? ipc, string? status, bool expected) =>
        Assert.Equal(expected, GatherStallWatch.IsWaiting(ipc, status));

    [Fact]
    public void Message_names_the_missing_items_and_the_status()
    {
        var message = GatherStallWatch.Message(["3x Mogpom"], "Fishing...", TimeSpan.FromMinutes(25.4));
        Assert.Equal("GatherBuddy seems stuck: the character has stood idle for 25 minutes and no count changed (GatherBuddy's status: " +
                     "\"Fishing...\"). Still missing: 3x Mogpom. Skip these items or get them separately (e.g. fish them with fish_until, " +
                     "buy them, or gather them by hand), then run gather_until again without them.", message);
    }

    [Fact]
    public void Message_without_status()
    {
        var message = GatherStallWatch.Message(["3x Mogpom", "2x Raw Ruby"], null, TimeSpan.FromMinutes(6));
        Assert.StartsWith("GatherBuddy seems stuck: the character has stood idle for 6 minutes and no count changed (GatherBuddy shows no status). " +
                          "Still missing: 3x Mogpom, 2x Raw Ruby.", message);
    }
}
