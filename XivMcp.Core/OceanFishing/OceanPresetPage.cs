using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XivMcp.OceanFishing;

/// <summary>What AutoHook's ocean fishing aims for (its OceanFishGoalKind names).</summary>
public enum OceanGoal { Points, Legendary, Achievement, Levelling }

/// <summary>One AutoHook export string from the wiki, with the heading it stood under.</summary>
public sealed record OceanPresetBlock(string Title, string Export);

/// <summary>
/// Reads AutoHook's wiki page "Ocean Fishing": its sections (### POINTS …, ### ACHIEVES, ### LEGENDS, ### LEVELING) hold AutoHook
/// export strings (AH…) in code blocks, one per stop or one folder for all.
/// </summary>
public static partial class OceanPresetPage
{
    public const string Url = "https://raw.githubusercontent.com/wiki/PunishXIV/AutoHook/Ocean-Fishing.md";

    public static string FolderName(OceanGoal goal) => $"XIV MCP Ocean - {goal}";

    /// <summary>A goal as people write it: points, legends/legendary, achieves/achievements, leveling/levelling.</summary>
    public static OceanGoal? Goal(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        if (t.StartsWith("POINT")) return OceanGoal.Points;
        if (t.StartsWith("LEGEND")) return OceanGoal.Legendary;
        if (t.StartsWith("ACHIEVE")) return OceanGoal.Achievement;
        if (t.StartsWith("LEVEL")) return OceanGoal.Levelling;
        return null;
    }

    public static Dictionary<OceanGoal, List<OceanPresetBlock>> Parse(string markdown)
    {
        var result = new Dictionary<OceanGoal, List<OceanPresetBlock>>();
        string? title = null;
        OceanGoal? goal = null;
        var inBlock = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```"))
            {
                inBlock = !inBlock;
                continue;
            }
            if (inBlock)
            {
                if (goal is { } g && title is not null && line.StartsWith("AH", StringComparison.Ordinal))
                {
                    if (!result.TryGetValue(g, out var list)) result[g] = list = [];
                    list.Add(new OceanPresetBlock(title, line));
                }
                continue;
            }
            if (Heading().Match(line) is { Success: true } m)
            {
                title = m.Groups[1].Value.Trim();
                goal = Goal(title.Split(' ', 2)[0]);
            }
        }
        return result;
    }

    [GeneratedRegex(@"^#{2,4}\s+(.+)$")]
    private static partial Regex Heading();
}
