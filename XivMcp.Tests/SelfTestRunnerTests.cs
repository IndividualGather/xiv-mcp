using XivMcp.Diagnostics;

namespace XivMcp.Tests;

public class SelfTestRunnerTests
{
    private static SelfTestCheck Check(string name, Func<CancellationToken, Task<string?>> run, TimeSpan? timeout = null) => new(name, run, timeout);

    [Fact]
    public async Task Runs_every_check_in_order_and_keeps_going_after_a_failure()
    {
        var results = await SelfTestRunner.RunAsync(
        [
            Check("a", _ => Task.FromResult<string?>("fine")),
            Check("b", _ => throw new InvalidOperationException("broken")),
            Check("c", _ => Task.FromResult<string?>(null)),
        ], default);

        Assert.Equal(["a", "b", "c"], results.Select(r => r.Name));
        Assert.Equal([SelfTestStatus.Passed, SelfTestStatus.Failed, SelfTestStatus.Passed], results.Select(r => r.Status));
        Assert.Equal("fine", results[0].Detail);
        Assert.Contains("broken", results[1].Detail);
        Assert.Equal("ok", results[2].Detail);
    }

    [Fact]
    public async Task Skips_are_reported_as_skipped()
    {
        var r = Assert.Single(await SelfTestRunner.RunAsync([Check("s", _ => throw new SelfTestSkip("not logged in"))], default));
        Assert.Equal(SelfTestStatus.Skipped, r.Status);
        Assert.Equal("not logged in", r.Detail);
    }

    [Fact]
    public async Task Slow_checks_fail_with_a_timeout()
    {
        var r = Assert.Single(await SelfTestRunner.RunAsync(
            [Check("slow", async ct => { await Task.Delay(5000, ct); return null; }, TimeSpan.FromMilliseconds(50))], default));
        Assert.Equal(SelfTestStatus.Failed, r.Status);
        Assert.Contains("timed out", r.Detail);
    }

    [Fact]
    public void Summary_counts_results()
    {
        var summary = SelfTestRunner.Summarize(
        [
            new("a", SelfTestStatus.Passed, "ok", TimeSpan.Zero),
            new("b", SelfTestStatus.Failed, "x", TimeSpan.Zero),
            new("c", SelfTestStatus.Skipped, "y", TimeSpan.Zero),
            new("d", SelfTestStatus.Passed, "ok", TimeSpan.Zero),
        ]);
        Assert.Equal("2 passed, 1 failed, 1 skipped", summary);
    }
}
