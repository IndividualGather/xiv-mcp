using XivMcp.Integrations;

namespace XivMcp.Tests;

public class FcchStateTests
{
    [Fact]
    public void Busy_on_the_home_world_is_busy()
    {
        Assert.True(FcchState.IsBusy(reportsBusy: true, currentWorld: 66, homeWorld: 66));
    }

    [Fact]
    public void Idle_is_idle()
    {
        Assert.False(FcchState.IsBusy(reportsBusy: false, currentWorld: 66, homeWorld: 66));
    }

    [Fact]
    public void Visiting_another_world_never_counts_as_busy()
    {
        // FCCH reports busy for as long as the character is on another world, where it can't reach a company chest anyway.
        Assert.False(FcchState.IsBusy(reportsBusy: true, currentWorld: 401, homeWorld: 66));
    }

    [Fact]
    public void An_unknown_world_trusts_what_fcch_reports()
    {
        Assert.True(FcchState.IsBusy(reportsBusy: true, currentWorld: 0, homeWorld: 66));
    }
}
