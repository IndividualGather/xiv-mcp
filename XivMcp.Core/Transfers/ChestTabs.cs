using System;
using System.Collections.Generic;

namespace XivMcp.Transfers;

/// <summary>
/// The company chest's item tabs. The game only sends a tab's items once it is selected in the chest window, so tools select the
/// tabs a move or a look needs first.
/// </summary>
public static class ChestTabs
{
    public const int Pages = 5;
    private const string PagePrefix = "FreeCompanyPage";

    /// <summary>The FreeCompanyChest node of the first item tab; the others follow (then Crystals and Gil).</summary>
    private const uint FirstTabNode = 10;

    /// <summary>The page (1-5) of a container name like FreeCompanyPage3, or null for anything else.</summary>
    public static int? Page(string? container) =>
        container is not null && container.StartsWith(PagePrefix, StringComparison.Ordinal)
        && int.TryParse(container.AsSpan(PagePrefix.Length), out var n) && n is >= 1 and <= Pages ? n : null;

    public static uint TabNode(int page) => FirstTabNode + (uint)(page - 1);

    /// <summary>
    /// The pages a move needs loaded: the ones it takes from and puts into, or all of them when the item is looked for everywhere
    /// (no source container given).
    /// </summary>
    public static List<int> PagesFor(string? from, string to, bool searchesAll)
    {
        if (searchesAll) return [1, 2, 3, 4, 5];
        var pages = new List<int>();
        foreach (var page in new[] { Page(from), Page(to) })
            if (page is { } p && !pages.Contains(p)) pages.Add(p);
        return pages;
    }
}
