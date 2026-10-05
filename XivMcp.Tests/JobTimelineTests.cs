using XivMcp.Jobs;

namespace XivMcp.Tests;

public class JobTimelineTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Working_time_adds_finished_steps_and_the_running_one_up_to_now()
    {
        var steps = new[]
        {
            new StepTiming(T0, T0.AddSeconds(30)),
            new StepTiming(T0.AddMinutes(1), T0.AddMinutes(3)),
            new StepTiming(T0.AddMinutes(5), null),   // running
            new StepTiming(null, null),               // queued
        };
        Assert.Equal(TimeSpan.FromSeconds(30 + 120 + 60), JobTimeline.Working(steps, T0.AddMinutes(6)));
    }

    [Fact]
    public void Working_time_ignores_clock_skew()
    {
        Assert.Equal(TimeSpan.Zero, JobTimeline.Working([new StepTiming(T0, T0.AddSeconds(-5))], T0));
    }

    [Fact]
    public void Elapsed_runs_until_now_or_until_the_job_ended()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), JobTimeline.Elapsed(T0, null, T0.AddMinutes(10)));
        Assert.Equal(TimeSpan.FromMinutes(4), JobTimeline.Elapsed(T0, T0.AddMinutes(4), T0.AddMinutes(10)));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(61, "1:01")]
    [InlineData(3599, "59:59")]
    [InlineData(3600 + 125, "1:02:05")]
    [InlineData(26 * 3600, "26:00:00")]
    public void Clock_reads_like_a_stopwatch(int seconds, string expected)
    {
        Assert.Equal(expected, JobTimeline.Clock(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(42, "42s")]
    [InlineData(192, "3m 12s")]
    [InlineData(7500, "2h 05m")]
    [InlineData(97200, "1d 3h")]
    public void Compact_durations_keep_the_two_largest_units(int seconds, string expected)
    {
        Assert.Equal(expected, JobTimeline.Compact(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Log_lines_split_into_time_and_text()
    {
        Assert.Equal(("12:03:04", "Step s1: wait started."), JobTimeline.SplitLog("12:03:04 Step s1: wait started."));
        Assert.Equal(("", "no time here"), JobTimeline.SplitLog("no time here"));
    }

    [Fact]
    public void Latest_progress_is_the_last_line_reported_by_that_step()
    {
        string[] log =
        [
            "12:00:00 Created with 2 step(s).",
            "12:00:01 Step farm: myplugin_farm started.",
            "12:00:05 farm: 3 kills",
            "12:00:09 farm: 7 kills",
            "12:00:10 other: 1 thing",
        ];
        Assert.Equal("7 kills", JobTimeline.LatestProgress(log, "farm"));
        Assert.Null(JobTimeline.LatestProgress(log, "missing"));
    }

    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(10, 2, 0)]
    [InlineData(10, 6, 4)]
    [InlineData(10, -1, 8)]   // finished: keep only the last two
    [InlineData(2, -1, 0)]
    public void Earlier_steps_fold_away_leaving_two_before_the_current(int count, int current, int hidden)
    {
        Assert.Equal(hidden, JobTimeline.HiddenBefore(count, current));
    }
}

public class StepWindowTests
{
    [Theory]
    [InlineData(5, 2, 3, 1, 4)]    // the step before, the current one, the one after
    [InlineData(5, 0, 3, 0, 3)]    // at the start: the next two
    [InlineData(5, 4, 3, 2, 5)]    // at the end: the two before
    [InlineData(2, 1, 3, 0, 2)]    // fewer steps than room
    [InlineData(5, -1, 3, 2, 5)]   // the job is over: the last ones
    [InlineData(30, 10, 12, 6, 18)] // a long job: some before, more ahead
    [InlineData(30, 1, 12, 0, 12)]
    [InlineData(30, 29, 12, 18, 30)]
    public void The_window_keeps_the_current_step_in_view(int count, int current, int size, int start, int end)
    {
        Assert.Equal((start, end), XivMcp.Jobs.JobTimeline.StepWindow(count, current, size));
    }
}
