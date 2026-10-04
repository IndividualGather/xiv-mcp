using System;

namespace XivMcp.Mcp;

/// <summary>Short forms of tool descriptions for the UI.</summary>
public static class ToolText
{
    private static readonly string[] Abbreviations = ["e.g.", "i.e.", "etc.", "vs."];

    /// <summary>The first sentence, however long: up to the first ". " that isn't part of an abbreviation like "e.g.".</summary>
    public static string FirstSentence(string text)
    {
        var from = 0;
        while (true)
        {
            var end = text.IndexOf(". ", from, StringComparison.Ordinal);
            if (end < 0) return text.Trim();
            var abbreviation = Array.Exists(Abbreviations, a => end + 1 >= a.Length && text.AsSpan(end + 1 - a.Length, a.Length).Equals(a, StringComparison.OrdinalIgnoreCase));
            if (!abbreviation) return text[..(end + 1)].Trim();
            from = end + 2;
        }
    }

    public enum BlockKind { Summary, Text, Bullet, Requirement }

    /// <summary>One paragraph or bullet of a formatted description.</summary>
    public sealed record Block(BlockKind Kind, string Text);

    private static readonly string[] RequirementStarts = ["Requires ", "Needs ", "Only available ", "Only works "];

    /// <summary>
    /// A description as paragraphs for display: the first sentence is the summary, the rest details, and sentences stating requirements
    /// ("Requires …", "Needs …", "Only available …") come last. A blank line starts a new paragraph; lines starting with "- " are bullets.
    /// </summary>
    public static System.Collections.Generic.List<Block> Paragraphs(string text)
    {
        var blocks = new System.Collections.Generic.List<Block>();
        var requirements = new System.Collections.Generic.List<string>();
        var first = true;
        foreach (var paragraph in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var prose = new System.Collections.Generic.List<string>();
            void FlushProse()
            {
                if (prose.Count == 0) return;
                AddSentences(string.Join(" ", prose));
                prose.Clear();
            }
            void AddSentences(string s)
            {
                var body = new System.Collections.Generic.List<string>();
                foreach (var sentence in Sentences(s))
                {
                    if (first) { blocks.Add(new Block(BlockKind.Summary, sentence)); first = false; }
                    else if (Array.Exists(RequirementStarts, r => sentence.StartsWith(r, StringComparison.Ordinal))) requirements.Add(sentence);
                    else body.Add(sentence);
                }
                if (body.Count > 0) blocks.Add(new Block(BlockKind.Text, string.Join(" ", body)));
            }
            foreach (var line in paragraph.Split('\n', StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    FlushProse();
                    if (first) first = false;
                    blocks.Add(new Block(BlockKind.Bullet, line[2..].Trim()));
                }
                else if (line.Length > 0) prose.Add(line);
            }
            FlushProse();
        }
        if (requirements.Count > 0) blocks.Add(new Block(BlockKind.Requirement, string.Join(" ", requirements)));
        return blocks;
    }

    /// <summary>Splits prose into sentences (respecting abbreviations like "e.g.").</summary>
    private static System.Collections.Generic.IEnumerable<string> Sentences(string text)
    {
        var rest = text.Trim();
        while (rest.Length > 0)
        {
            var sentence = FirstSentence(rest);
            yield return sentence;
            rest = rest[Math.Min(rest.Length, sentence.Length)..].Trim();
        }
    }

    /// <summary>At most <paramref name="max"/> characters, cut at a word and ending in "…".</summary>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', Math.Max(0, max - 1));
        return (cut > max / 2 ? text[..cut] : text[..(max - 1)]).TrimEnd(' ', ',', ';', '—') + "…";
    }
}
