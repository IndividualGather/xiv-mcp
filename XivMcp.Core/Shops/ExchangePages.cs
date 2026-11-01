using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Shops;

/// <summary>
/// The pages of a multi-page exchange (the scrip exchange's InclusionShop window) as the window lists them: the categories from
/// value 3 on, and the selected category's sub-pages from value 101 on, the first of them the "Select a subcategory" prompt. A
/// sub-page's position in that list is what the window's sub-page callback takes, and its name is the name of the SpecialShop it
/// shows — so the page that sells an item is found by name, not by its row index in the game data (which can differ).
/// </summary>
public static class ExchangePages
{
    public const int FirstCategory = 3;
    public const int FirstSubPage = 101;
    private const int MaxEntries = 30;

    public static IReadOnlyList<string> Categories(Func<int, string?> text) => Read(text, FirstCategory);

    /// <summary>The selected category's sub-pages, the prompt at index 0; empty when the category has none.</summary>
    public static IReadOnlyList<string> SubPages(Func<int, string?> text) => Read(text, FirstSubPage);

    /// <summary>Index of the sub-page showing one of these shops (never the prompt at 0), or null.</summary>
    public static int? SubPageOf(IReadOnlyList<string> subPages, IEnumerable<string> shopNames)
    {
        var names = shopNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < subPages.Count; i++)
            if (names.Contains(subPages[i].Trim())) return i;
        return null;
    }

    private static List<string> Read(Func<int, string?> text, int first)
    {
        var list = new List<string>();
        for (var i = first; i < first + MaxEntries && text(i) is { Length: > 0 } s; i++) list.Add(s);
        return list;
    }
}
