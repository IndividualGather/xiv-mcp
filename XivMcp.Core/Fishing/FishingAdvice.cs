using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Fishing;

/// <summary>Step-by-step advice for catching a fish, and a counting macro for telling its bite apart by time.</summary>
public sealed record CatchAdvice(IReadOnlyList<string> Steps, IReadOnlyList<string> Abilities, string? CountingMacro);

public static class FishingAdvice
{
    /// <summary>Advice for a guide. <paramref name="item"/> names items and fish, <paramref name="weather"/> weathers, <paramref name="folklore"/> folklore tomes.</summary>
    public static CatchAdvice For(FishGuide g, Func<uint, string> item, Func<uint, string> weather, Func<uint, string>? folklore = null)
    {
        var steps = new List<string>();
        var abilities = new List<string>();
        var target = item(g.Fish);

        if (g.Folklore is { } tome) steps.Add($"You need to have read {folklore?.Invoke(tome) ?? "its folklore tome"} to catch {target}.");
        if (g.Lure is { } lure) steps.Add($"{target} only bites under {lure} Lure: use it before the bite.");

        if (When(g.Conditions, weather) is { } when) steps.Add($"{target} only bites {when}.");

        if (g.Predators.Count > 0)
        {
            var list = Join(g.Predators.Select(p => $"{p.Count} {item(p.Fish)}"), "and");
            var length = g.IntuitionSeconds is { } s ? $" It lasts {s} seconds." : "";
            steps.Add($"First catch {list} to get Fisher's Intuition; {target} only bites while it lasts.{length}");
            foreach (var p in g.PredatorCasts)
            {
                var how = p.Mooch ? $"Mooch with {item(p.Bait)}" : $"Cast with {item(p.Bait)}";
                var tug = p.Tug is { } t ? $" and hook the {Tugs.Describe(t)} bite" : "";
                var hookset = p.Hookset is { } h ? $" with {h} Hookset" : "";
                steps.Add($"{how}{tug}{hookset} to catch {item(p.Fish)}.");
            }
            abilities.Add("Identical Cast after catching one of the fish for Fisher's Intuition makes the next one more likely.");
        }

        for (var i = 0; i < g.Path.Count; i++)
        {
            var s = g.Path[i];
            var cast = s.Mooch ? $"Mooch with the {item(s.Bait)} you just caught" : $"Cast with {item(s.Bait)}";
            var hook = s.Tug is { } tug ? $"hook the {Tugs.Describe(tug)} bite" : "hook the bite";
            var hookset = s.Hookset is { } h ? $" with {h} Hookset" : "";
            var timing = s.Bite is { } b ? $" It bites after about {b.Describe()}." : "";
            steps.Add($"{cast} and {hook}{hookset} to catch {item(s.Fish)}.{timing}");
        }

        if (g.Snagging) abilities.Add("Snagging: this fish is only caught with Snagging on.");
        if (g.Collectable) abilities.Add("Collect (Collector's Glove) to catch it as a collectable for scrips.");
        if (g.BigFish)
        {
            abilities.Add("Patience II before casting: big fish escape a normal Hook more easily, so hook with the hookset named above.");
            abilities.Add("Chum makes bites come sooner, which helps in short windows; the bite times above are without Chum.");
        }
        if (g.FishEyes && g.Conditions.Timed) abilities.Add("Fish Eyes lets it bite outside its time window (the weather still has to be right).");

        var last = g.Last;
        var sameTug = g.Rivals.Where(r => r.Tug is not null && r.Tug == last.Tug).ToList();
        string? macro = null;
        if (sameTug.Count > 0)
        {
            var names = string.Join(", ", sameTug.Select(r => r.Bite is { } b ? $"{item(r.Fish)} ({b.Describe()})" : item(r.Fish)));
            steps.Add($"Other fish bite here with the same {Tugs.Signs(last.Tug!.Value)}: {names}.");
            if (last.Bite is { } mine && sameTug.All(r => r.Bite is { } b && !b.Overlaps(mine)))
            {
                steps.Add($"Tell them apart by time: only hook a {Tugs.Signs(last.Tug!.Value)} between {mine.Describe()} after casting, and reel in otherwise.");
                macro = CountingMacro(mine.Max);
            }
            abilities.Add($"Surface Slap after catching {item(sameTug[0].Fish)} makes it less likely to bite again.");
        }

        return new CatchAdvice(steps, abilities, macro);
    }

    /// <summary>When the conditions allow a bite, in words: "from 16:00 to 20:00 Eorzean time, in Rain after Clouds".</summary>
    public static string? When(FishConditions c, Func<uint, string> weather)
    {
        var parts = new List<string>();
        if (c.Timed) parts.Add($"from {EorzeaTime.Format(c.StartHour)} to {EorzeaTime.Format(c.EndHour)} Eorzean time");
        if (c.Weathers.Count > 0)
        {
            var now = $"in {Or(c.Weathers.Select(weather))}";
            if (c.PreviousWeathers.Count > 0) now += $" after {Or(c.PreviousWeathers.Select(weather))}";
            parts.Add(now);
        }
        else if (c.PreviousWeathers.Count > 0) parts.Add($"after {Or(c.PreviousWeathers.Select(weather))}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// A macro that casts and echoes the seconds in chat, so the player can see when a bite comes. Macros have 15 lines and wait at
    /// most 60 seconds per line.
    /// </summary>
    public static string CountingMacro(double untilSeconds)
    {
        var total = (int)Math.Min(Math.Ceiling(untilSeconds) + 2, 60);
        var step = Math.Max(1, (int)Math.Ceiling(total / 14.0));
        var lines = new List<string> { "/ac Cast <wait.1>" };
        for (var s = 1; s <= total && lines.Count < 15; s += step) lines.Add($"/echo {s} s <wait.{step}>");
        return string.Join("\n", lines);
    }

    private static string Or(IEnumerable<string> items) => Join(items, "or");

    /// <summary>"A", "A or B", "A, B or C".</summary>
    private static string Join(IEnumerable<string> items, string word)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} {word} {list[^1]}";
    }
}
