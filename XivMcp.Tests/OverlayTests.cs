using XivMcp.Ui;

namespace XivMcp.Tests;

public class ActivityTrackerTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan ShowAfter = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(3);

    [Fact]
    public void A_running_call_shows_once_it_has_run_longer_than_the_delay()
    {
        var t = new ActivityTracker();
        t.Begin("navigate_to", "claude-code", T0);
        Assert.Empty(t.Visible(T0.AddMilliseconds(100), ShowAfter, Linger));
        var shown = Assert.Single(t.Visible(T0.AddSeconds(1), ShowAfter, Linger));
        Assert.Equal("navigate_to", shown.Tool);
        Assert.True(shown.Running);
    }

    [Fact]
    public void Quick_calls_never_show()
    {
        var t = new ActivityTracker();
        var id = t.Begin("get_character", null, T0);
        t.End(id, failed: false, T0.AddMilliseconds(30));
        Assert.Empty(t.Visible(T0.AddMilliseconds(500), ShowAfter, Linger));
    }

    [Fact]
    public void Finished_long_calls_linger_then_disappear()
    {
        var t = new ActivityTracker();
        var id = t.Begin("navigate_to", null, T0);
        t.End(id, failed: true, T0.AddSeconds(10));
        var done = Assert.Single(t.Visible(T0.AddSeconds(12), ShowAfter, Linger));
        Assert.False(done.Running);
        Assert.True(done.Failed);
        Assert.Empty(t.Visible(T0.AddSeconds(14), ShowAfter, Linger));
    }

    [Fact]
    public void Running_calls_come_first_newest_first()
    {
        var t = new ActivityTracker();
        var a = t.Begin("a", null, T0);
        t.Begin("b", null, T0.AddSeconds(1));
        t.Begin("c", null, T0.AddSeconds(2));
        t.End(a, false, T0.AddSeconds(3));
        Assert.Equal(["c", "b", "a"], t.Visible(T0.AddSeconds(4), ShowAfter, Linger).Select(c => c.Tool));
    }

    [Fact]
    public void Running_reports_any_call_in_flight_regardless_of_the_delay()
    {
        var t = new ActivityTracker();
        Assert.False(t.AnyRunning);
        var id = t.Begin("x", null, T0);
        Assert.True(t.AnyRunning);
        t.End(id, false, T0);
        Assert.False(t.AnyRunning);
    }
}

public class OverlaySettingsTests
{
    [Fact]
    public void Defaults_show_a_full_overlay_that_can_be_moved_and_clicked()
    {
        var s = new OverlaySettings();
        Assert.True(s.Enabled);
        Assert.Equal(OverlayLayout.Full, s.Layout);
        Assert.True(s.CanMove);
        Assert.True(s.CanClick);
    }

    [Fact]
    public void Click_through_locks_the_overlay_and_disables_its_buttons()
    {
        var s = new OverlaySettings { ClickThrough = true };
        Assert.False(s.CanMove);
        Assert.False(s.CanClick);
    }

    [Fact]
    public void Lock_keeps_the_buttons_and_no_interaction_keeps_moving()
    {
        Assert.True(new OverlaySettings { Locked = true }.CanClick);
        Assert.False(new OverlaySettings { Locked = true }.CanMove);
        Assert.False(new OverlaySettings { Interactive = false }.CanClick);
        Assert.True(new OverlaySettings { Interactive = false }.CanMove);
    }

    [Fact]
    public void Out_of_range_values_from_an_edited_file_are_clamped()
    {
        var s = new OverlaySettings { Scale = 9, Opacity = -1, ShowAfterMs = -5, LingerSeconds = 999, MaxItems = 0 };
        s.Normalize();
        Assert.Equal(OverlaySettings.MaxScale, s.Scale);
        Assert.Equal(OverlaySettings.MinOpacity, s.Opacity);
        Assert.Equal(0, s.ShowAfterMs);
        Assert.Equal(30, s.LingerSeconds);
        Assert.Equal(1, s.MaxItems);
    }

    [Theory]
    [InlineData(true, false, 0, 0, false)]  // nothing running
    [InlineData(true, false, 1, 0, true)]   // a call
    [InlineData(true, false, 0, 1, true)]   // a job
    [InlineData(false, false, 1, 1, false)] // turned off
    [InlineData(false, true, 0, 0, true)]   // the preview shows even when off and idle
    public void The_overlay_only_shows_while_something_is_active(bool enabled, bool preview, int calls, int jobs, bool shown)
    {
        Assert.Equal(shown, new OverlaySettings { Enabled = enabled }.ShouldShow(preview, calls, jobs));
    }

    [Fact]
    public void Hidden_kinds_do_not_open_the_overlay()
    {
        Assert.False(new OverlaySettings { ShowToolCalls = false }.ShouldShow(false, 2, 0));
        Assert.False(new OverlaySettings { ShowJobs = false }.ShouldShow(false, 0, 2));
    }
}
