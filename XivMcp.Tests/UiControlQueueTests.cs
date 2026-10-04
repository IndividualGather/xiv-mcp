using XivMcp.Ui;

namespace XivMcp.Tests;

public class UiControlQueueTests
{
    [Fact]
    public async Task A_requested_control_is_pressed_the_next_time_it_is_drawn()
    {
        var q = new UiControlQueue();
        var press = q.Request("job:1:pause", TimeSpan.FromSeconds(5));
        q.BeginFrame();
        Assert.False(q.Consume("job:1:cancel"));
        Assert.True(q.Consume("job:1:pause"));
        Assert.True(await press);
        Assert.False(q.Consume("job:1:pause")); // only once
    }

    [Fact]
    public async Task A_control_that_is_never_drawn_times_out()
    {
        var q = new UiControlQueue();
        Assert.False(await q.Request("tab:Nowhere", TimeSpan.FromMilliseconds(50)));
        q.BeginFrame();
        Assert.False(q.Consume("tab:Nowhere")); // a late draw doesn't press it any more
    }

    [Fact]
    public void Visible_controls_are_the_ones_drawn_in_the_last_full_frame()
    {
        var q = new UiControlQueue();
        q.BeginFrame();
        q.Consume("tab:Jobs");
        q.Consume("job:1:pause");
        q.Consume("job:1:pause"); // drawn twice, listed once
        Assert.Empty(q.Visible); // the frame isn't over yet
        q.BeginFrame();
        Assert.Equal(["job:1:pause", "tab:Jobs"], q.Visible.Order());
        q.BeginFrame();
        Assert.Empty(q.Visible); // nothing drawn: the window closed
    }

    [Fact]
    public void Ids_ignore_case()
    {
        var q = new UiControlQueue();
        _ = q.Request("TAB:jobs", TimeSpan.FromSeconds(5));
        q.BeginFrame();
        Assert.True(q.Consume("tab:Jobs"));
    }
}
