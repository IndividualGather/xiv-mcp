using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace XivMcp.Ui;

/// <summary>
/// Recognizes a game prompt by the text sheet row it was built from, in any client language: the row's fixed text pieces (the
/// parts around its placeholders, such as item names) must all appear in the prompt, in order. Whitespace and case don't count,
/// since windows wrap lines.
/// </summary>
public static class PromptTemplate
{
    public static bool Matches(string prompt, IReadOnlyList<string> pieces)
    {
        var fixedPieces = pieces.Select(Normalize).Where(p => p.Length > 0).ToList();
        if (fixedPieces.Count == 0) return false;
        var text = Normalize(prompt);
        var at = 0;
        foreach (var piece in fixedPieces)
        {
            var found = text.IndexOf(piece, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            at = found + piece.Length;
        }
        return true;
    }

    private static string Normalize(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
