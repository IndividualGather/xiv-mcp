using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.TripleTriad;

/// <summary>A card for a deck: its id (TripleTriadCard row), name, stars (1–5) and whether the player has it.</summary>
public sealed record TriadDeckCard(int Id, string Name, int Stars, bool Owned);

/// <summary>
/// The game's rules for a Triple Triad deck: 5 different cards the player has, at most one 5-star card, and at most two cards of
/// 4 stars or more. Writing a deck preset bypasses the game's own check, so XIV MCP checks first.
/// </summary>
public static class TriadDeckRules
{
    public const int Size = 5;

    /// <summary>What is wrong with the deck, or null if the game accepts it.</summary>
    public static string? Problem(IReadOnlyList<TriadDeckCard> cards)
    {
        if (cards.Count != Size) return $"A deck has exactly {Size} cards, not {cards.Count}.";
        if (cards.FirstOrDefault(c => !c.Owned) is { } missing) return $"You don't have {missing.Name} yet.";
        if (cards.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1) is { } twice) return $"{twice.First().Name} is in the deck twice.";
        if (cards.Count(c => c.Stars >= 5) > 1) return "A deck can hold only one 5-star card.";
        if (cards.Count(c => c.Stars >= 4) > 2) return "A deck can hold at most two cards of 4-star or more.";
        return null;
    }
}

/// <summary>Jumbo Cactpot ticket numbers: four digits each, at most three tickets a week.</summary>
public static class JumboTickets
{
    public const int MaxTickets = 3;

    public static bool IsValid(string number) => number.Length == 4 && number.All(char.IsAsciiDigit);

    /// <summary>The price of the week's 1st, 2nd and 3rd ticket in MGP.</summary>
    public static readonly int[] Prices = [100, 150, 200];

    /// <summary>
    /// How many tickets an amount of MGP bought. Every run of consecutive prices has its own sum (100, 250, 450 from the first ticket;
    /// 150, 350 from the second; 200 for the third), so the count is clear even when some were bought earlier in the week. 0 if no run matches.
    /// </summary>
    public static int CountFromSpent(long spent)
    {
        for (var first = 0; first < Prices.Length; first++)
        {
            long sum = 0;
            for (var n = 1; first + n <= Prices.Length; n++)
                if ((sum += Prices[first + n - 1]) == spent) return n;
        }
        return 0;
    }

    public static IReadOnlyList<string> Parse(IEnumerable<string> numbers)
    {
        var list = numbers.Select(n => n.Trim()).ToList();
        if (list.Count > MaxTickets) throw new ArgumentException($"At most {MaxTickets} tickets can be bought a week.");
        if (list.FirstOrDefault(n => !IsValid(n)) is { } bad) throw new ArgumentException($"'{bad}' is not a ticket number: use 4 digits, like 0427.");
        return list;
    }
}

/// <summary>Finding one entry by a name the player or assistant typed: an exact name first, then a unique part of one.</summary>
public static class NameMatch
{
    public static T Single<T>(IEnumerable<T> items, string query, Func<T, string> name)
    {
        var all = items.ToList();
        var q = query.Trim();
        var exact = all.Where(i => string.Equals(name(i), q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count >= 1) return exact[0];
        var partial = all.Where(i => name(i).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new ArgumentException($"Nothing is called '{q}'."),
            _ => throw new ArgumentException($"'{q}' matches several: {string.Join(", ", partial.Select(name).Distinct().Take(10))}. Give the full name."),
        };
    }
}
