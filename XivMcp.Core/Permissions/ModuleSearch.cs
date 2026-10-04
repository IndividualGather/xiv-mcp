using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Permissions;

/// <summary>
/// The search field of the Modules tab: which sections (permission groups) to show and, within them, which tool calls. Every word of
/// the query has to appear (in any order, ignoring case) in the section's title or description, or in a tool's name or summary.
/// </summary>
public static class ModuleSearch
{
    /// <summary>A tool call as the search sees it: its name and the player-facing summary.</summary>
    public sealed record Tool(string Name, string? Summary);

    /// <summary>
    /// Whether the section is shown. <see cref="Tools"/>: null to show its tools as usual (empty query, or the section itself matches);
    /// otherwise only these tools, with their rows opened.
    /// </summary>
    public sealed record Result(bool Visible, IReadOnlyList<string>? Tools);

    private static readonly Result Everything = new(true, null);

    public static Result Match(string query, string title, string description, IReadOnlyList<Tool> tools)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return Everything;

        var section = $"{title} {description}";
        var matching = tools.Where(t => words.All(w => Contains(section, w) || Contains(ToolText(t), w)) && words.Any(w => Contains(ToolText(t), w)))
                            .Select(t => t.Name).ToList();
        if (matching.Count > 0) return new Result(true, matching);
        return words.All(w => Contains(section, w)) ? Everything : new Result(false, []);
    }

    /// <summary>What a tool matches on: its name as written and with spaces (set_map_flag, "set map flag"), and its summary.</summary>
    private static string ToolText(Tool t) => $"{t.Name} {t.Name.Replace('_', ' ')} {t.Summary}";

    private static bool Contains(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
