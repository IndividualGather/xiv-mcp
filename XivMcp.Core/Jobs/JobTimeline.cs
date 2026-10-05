using System;
using System.Collections.Generic;

namespace XivMcp.Jobs;

/// <summary>When a step ran: <see cref="Started"/> is null for a step that hasn't run, <see cref="Finished"/> null while it runs.</summary>
public readonly record struct StepTiming(DateTime? Started, DateTime? Finished);

/// <summary>Times, durations and log lines of a background job, as the Jobs tab shows them.</summary>
public static class JobTimeline
{
    /// <summary>Steps kept visible before the current one; earlier ones fold into "N earlier steps".</summary>
    public const int KeepBefore = 2;

    /// <summary>Time spent running steps: finished steps in full, the running one up to <paramref name="now"/>. Pauses don't count.</summary>
    public static TimeSpan Working(IEnumerable<StepTiming> steps, DateTime now)
    {
        var total = TimeSpan.Zero;
        foreach (var s in steps)
            if (s.Started is { } start)
            {
                var span = (s.Finished ?? now) - start;
                if (span > TimeSpan.Zero) total += span;
            }
        return total;
    }

    /// <summary>Time since the job was created, until <paramref name="ended"/> once it is over.</summary>
    public static TimeSpan Elapsed(DateTime created, DateTime? ended, DateTime now)
    {
        var span = (ended ?? now) - created;
        return span > TimeSpan.Zero ? span : TimeSpan.Zero;
    }

    /// <summary>A stopwatch: "0:42", "12:05", "1:02:05" (hours keep counting past a day).</summary>
    public static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>The two largest units: "42s", "3m 12s", "2h 05m", "1d 3h".</summary>
    public static string Compact(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds}s"
        : t.TotalHours < 1 ? $"{t.Minutes}m {t.Seconds:00}s"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : $"{(int)t.TotalDays}d {t.Hours}h";

    /// <summary>A log line "HH:mm:ss text" as its time and its text (time empty when the line has none).</summary>
    public static (string Time, string Text) SplitLog(string line) =>
        line.Length > 9 && line[2] == ':' && line[5] == ':' && line[8] == ' ' ? (line[..8], line[9..]) : ("", line);

    /// <summary>The last progress line a step reported ("stepId: text" in the log), or null.</summary>
    public static string? LatestProgress(IReadOnlyList<string> log, string stepId)
    {
        var prefix = stepId + ": ";
        for (var i = log.Count - 1; i >= 0; i--)
        {
            var text = SplitLog(log[i]).Text;
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return text[prefix.Length..];
        }
        return null;
    }

    /// <summary>
    /// How many steps at the start fold away: all but <see cref="KeepBefore"/> before the current step (index
    /// <paramref name="current"/>), or all but the last two when there is none (-1, the job is over).
    /// </summary>
    public static int HiddenBefore(int count, int current) =>
        Math.Max(0, (current < 0 ? count : current) - KeepBefore);

    /// <summary>
    /// Which steps to show when there is room for <paramref name="size"/>: [start, end), with the current step in view (a third of
    /// the room before it, the rest ahead) and the room filled at either end. A finished job (-1) shows its last steps.
    /// </summary>
    public static (int Start, int End) StepWindow(int count, int current, int size)
    {
        if (count <= size) return (0, count);
        var at = current < 0 ? count - 1 : current;
        var start = Math.Max(0, at - size / 3);
        var end = Math.Min(count, start + size);
        return (Math.Max(0, end - size), end);
    }
}
