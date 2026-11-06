using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Shops;

/// <summary>A menu entry to select: its index, and the name (menu path part or shop name) it matched, null for a guess.</summary>
public sealed record MenuChoice(int Index, string? Matched);

/// <summary>
/// Finding the way through a vendor's menus to the shop. Some vendors show a topic menu first ("Purchase Disciple of the Hand
/// Tools") and the shops behind it on a second menu ("Purchase Tools (Lv. 1-9)"); every level is matched against the caller's
/// menu path first, then against the names of the shops that sell the item (topic name before shop name).
/// </summary>
public static class ShopMenu
{
    /// <summary>Separates the levels in a menu_option path: "Purchase Disciple of the Hand Tools &gt; Purchase Tools (Lv. 1-9)".</summary>
    public const char PathSeparator = '>';

    /// <summary>The levels of a menu_option, trimmed; empty when none is given.</summary>
    public static List<string> ParsePath(string? menuOption) =>
        menuOption is null ? [] : menuOption.Split(PathSeparator).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    /// <summary>
    /// The entry to select on the menu that is shown, or null when it can't be told. Names already used on an earlier level are
    /// skipped (so a level never picks the topic again); without a matching name, the only entry that looks like buying is taken.
    /// </summary>
    public static MenuChoice? Pick(IReadOnlyList<string> entries, IReadOnlyList<string> path, IReadOnlyList<string> shopNames,
                                   IReadOnlyCollection<string> used)
    {
        foreach (var name in path.Concat(shopNames).Where(n => n.Length > 0 && !used.Contains(n, StringComparer.OrdinalIgnoreCase)))
            if (Match(entries, name) is var index and >= 0)
                return new MenuChoice(index, name);
        var buying = entries.Select((e, i) => (e, i)).Where(x => LooksLikeBuying(x.e) && !used.Contains(x.e, StringComparer.OrdinalIgnoreCase)).ToList();
        return buying.Count == 1 ? new MenuChoice(buying[0].i, null) : null;
    }

    /// <summary>How many to buy in one go: up to the batch limit, but one at a time for items that don't stack (gear).</summary>
    public static int BatchSize(int remaining, int stackSize, int maxBatch) =>
        remaining <= 0 ? 0 : stackSize <= 1 ? 1 : Math.Min(maxBatch, remaining);

    /// <summary>Exact match, then a prefix either way (entries carry level ranges), then containment.</summary>
    private static int Match(IReadOnlyList<string> entries, string name)
    {
        var list = entries.ToList();
        var exact = list.FindIndex(e => e.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;
        var prefix = list.FindIndex(e => e.StartsWith(name, StringComparison.OrdinalIgnoreCase) ||
                                         name.StartsWith(e, StringComparison.OrdinalIgnoreCase) && e.Length > 4);
        if (prefix >= 0) return prefix;
        return list.FindIndex(e => e.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeBuying(string entry) =>
        entry.Contains("Purchase", StringComparison.OrdinalIgnoreCase) || entry.Contains("Buy", StringComparison.OrdinalIgnoreCase) ||
        entry.Contains("Exchange", StringComparison.OrdinalIgnoreCase) || entry.Contains("Trade", StringComparison.OrdinalIgnoreCase);
}
