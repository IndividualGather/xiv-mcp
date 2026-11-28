using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Fashion;

/// <summary>
/// How the Masked Rose scores an outfit, as players have worked it out (it is not in the game data): each of the 11 judged slots is
/// worth 10 points (weapon, head, body, hands, legs, feet) or 8 (earrings, necklace, bracelets, each ring). A slot with nothing in it
/// scores 0. A slot the week gives a hint for scores in full only for a piece that matches it, and 2 for any other piece. Each of the
/// six left-side slots also gets 2 points for the week's exact dye color. The score is capped at 100.
/// </summary>
public static class FashionScore
{
    public const int Cap = 100, Participation = 2, ExactDye = 2;

    public static readonly int[] Judged =
    [
        GearSlots.MainHand, GearSlots.Head, GearSlots.Body, GearSlots.Hands, GearSlots.Legs, GearSlots.Feet,
        GearSlots.Ears, GearSlots.Neck, GearSlots.Wrists, GearSlots.RingRight, GearSlots.RingLeft,
    ];

    /// <summary>Slots whose dye counts: the weapon and the five armour slots.</summary>
    public static bool DyeCounts(int slot) => slot is GearSlots.MainHand or GearSlots.Head or GearSlots.Body or GearSlots.Hands or GearSlots.Legs or GearSlots.Feet;

    public static int Max(int slot) => DyeCounts(slot) ? 10 : 8;

    /// <summary>The points of a piece (or of nothing) in a slot, its dye left out.</summary>
    public static int Piece(int slot, bool hinted, bool filled, bool matches) => !filled ? 0 : hinted && !matches ? Participation : Max(slot);
}

/// <summary>Where a candidate piece is, in the order the planner prefers when two score the same (a dungeon that drops it last).</summary>
public enum PieceSource { Empty, Worn, Carried, Retainer, Armoire, Dresser, Vendor, Market, Duty }

/// <summary>What happens to a piece's dye: nothing, it already has the week's color, it gets dyed (it has no dye yet or was bought
/// for the report), or an existing dye of another color is replaced (only once the player approves).</summary>
public enum DyeChange { None, AlreadyRight, Dye, Replace }

/// <summary>
/// A piece the planner may put in a slot. <see cref="Stain"/> and <see cref="Stain2"/> are its dyes in the two channels (0 = none,
/// null = not known, e.g. an older retainer snapshot); <see cref="Channels"/> how many it has (0 = takes no dye); <see cref="Price"/>
/// what buying it costs (vendor price, or the lowest market listing when known); <see cref="Copies"/> how many the player has
/// (rings can be worn twice). The judging counts the better of the two channels.
/// </summary>
public sealed record FashionCandidate(uint ItemId, string Name, PieceSource Source, bool Matches, byte? Stain, int Channels,
                                      long? Price = null, string? Retainer = null, int Copies = 1, byte? Stain2 = 0);

/// <summary>One slot to plan: whether it has a hint, the week's exact dye for it (null when it has none or it doesn't count), and the
/// pieces it may take (the worn one included, when it can stay).</summary>
public sealed record FashionSlot(int Slot, bool Hinted, uint? WantedStain, IReadOnlyList<FashionCandidate> Candidates);

/// <summary>
/// What the planner chose for a slot: the piece (null = left empty), what happens to its dye and in which channel, and the points it
/// brings.
/// </summary>
public sealed record FashionChoice(int Slot, FashionCandidate? Piece, DyeChange Dye, int Points, long Cost, int Channel = 1);

/// <summary>The chosen outfit: one choice per judged slot, the expected score (capped), and whether it reaches the target.</summary>
public sealed record FashionOutfit(IReadOnlyList<FashionChoice> Choices, int Score, bool ReachesTarget);

/// <summary>
/// Picks the cheapest outfit that reaches a score: pieces already worn cost nothing, then pieces carried, then a trip to a retainer,
/// the armoire or the dresser, then buying (vendor or market board, by price). Dyeing costs a dye, and buying one if the bags have
/// none; replacing an existing dye of another color costs far more than anything else, so it is only chosen when nothing else reaches
/// the target. Slots are independent apart from the target, so one pass over the slots with the score so far finds the cheapest mix.
/// </summary>
public static class FashionPlanner
{
    public const long Carry = 1, Trip = 20, Buy = 1_000, DyeOwned = 2, ReplaceDye = 1_000_000;

    /// <summary>
    /// The outfit for <paramref name="target"/> points. <paramref name="dyeCost"/> is what a dye of a color costs (one in the bags, or
    /// one to buy). Without <paramref name="allowReplace"/>, existing dyes of another color are never replaced. When no outfit reaches
    /// the target, the best-scoring one is returned with <see cref="FashionOutfit.ReachesTarget"/> false.
    /// </summary>
    public static FashionOutfit Plan(IReadOnlyList<FashionSlot> slots, int target, Func<uint, long> dyeCost, bool allowReplace = true)
    {
        target = Math.Clamp(target, 0, FashionScore.Cap);
        var banned = new HashSet<(int Slot, uint Item)>();
        for (var round = 0; ; round++)
        {
            var options = slots.Select(s => Options(s, dyeCost, allowReplace).Where(o => o.Piece is null || !banned.Contains((s.Slot, o.Piece.ItemId))).ToList()).ToList();
            var outfit = Solve(options, target);
            // A ring the player has once can't be worn on both hands: keep it on one and plan the other again.
            var clash = outfit.Choices.Where(c => c.Piece is { Source: PieceSource.Carried or PieceSource.Retainer or PieceSource.Armoire or PieceSource.Dresser } p && p.Copies < 2)
                .GroupBy(c => c.Piece!.ItemId).FirstOrDefault(g => g.Count() > 1);
            if (clash is null || round >= 5) return outfit;
            banned.Add((clash.Last().Slot, clash.Key));
        }
    }

    /// <summary>The ways to fill a slot: each candidate with and without dyeing, and leaving it empty.</summary>
    public static IEnumerable<FashionChoice> Options(FashionSlot slot, Func<uint, long> dyeCost, bool allowReplace)
    {
        var worn = slot.Candidates.Any(c => c.Source == PieceSource.Worn);
        if (!worn) yield return new FashionChoice(slot.Slot, null, DyeChange.None, 0, 0);
        foreach (var piece in slot.Candidates)
        {
            var points = FashionScore.Piece(slot.Slot, slot.Hinted, true, piece.Matches);
            var cost = SourceCost(piece);
            var dyeCounts = FashionScore.DyeCounts(slot.Slot) && slot.WantedStain is not null;
            if (dyeCounts && piece.Channels > 0 && (piece.Stain == slot.WantedStain || piece.Channels > 1 && piece.Stain2 == slot.WantedStain))
            {
                yield return new FashionChoice(slot.Slot, piece, DyeChange.AlreadyRight, points + FashionScore.ExactDye, cost);
                continue;
            }
            yield return new FashionChoice(slot.Slot, piece, DyeChange.None, points, cost);
            if (!dyeCounts || piece.Channels == 0 || piece.Stain is not { } stain) continue;
            // An empty channel takes the color without touching a dye the player put on; a bought piece has none.
            var (change, channel) =
                stain == 0 || piece.Source is PieceSource.Vendor or PieceSource.Market or PieceSource.Duty ? (DyeChange.Dye, 1)
                : piece.Channels > 1 && piece.Stain2 == 0 ? (DyeChange.Dye, 2)
                : (DyeChange.Replace, 1);
            if (change == DyeChange.Replace && !allowReplace) continue;
            yield return new FashionChoice(slot.Slot, piece, change, points + FashionScore.ExactDye,
                cost + dyeCost(slot.WantedStain!.Value) + (change == DyeChange.Replace ? ReplaceDye : 0), channel);
        }
    }

    public static long SourceCost(FashionCandidate piece) => piece.Source switch
    {
        PieceSource.Worn or PieceSource.Empty => 0,
        PieceSource.Carried => Carry,
        PieceSource.Retainer or PieceSource.Armoire or PieceSource.Dresser => Trip,
        PieceSource.Duty => Buy * 1_000,
        _ => Buy + (piece.Price ?? 0),
    };

    /// <summary>Cheapest mix reaching the target: cost by score so far (capped at the target), slot after slot.</summary>
    private static FashionOutfit Solve(List<List<FashionChoice>> options, int target)
    {
        const long none = long.MaxValue;
        var cost = Enumerable.Repeat(none, target + 1).ToArray();
        cost[0] = 0;
        var from = new List<(int Prev, int Option)[]>();
        foreach (var slot in options)
        {
            var next = Enumerable.Repeat(none, target + 1).ToArray();
            var back = new (int, int)[target + 1];
            for (var score = 0; score <= target; score++)
            {
                if (cost[score] == none) continue;
                for (var o = 0; o < slot.Count; o++)
                {
                    var to = Math.Min(target, score + slot[o].Points);
                    var c = cost[score] + slot[o].Cost;
                    if (c < next[to]) { next[to] = c; back[to] = (score, o); }
                }
            }
            cost = next;
            from.Add(back);
        }
        var end = Enumerable.Range(0, target + 1).Last(s => cost[s] != none);
        var chosen = new FashionChoice[options.Count];
        for (int i = options.Count - 1, at = end; i >= 0; i--)
        {
            var (prev, o) = from[i][at];
            chosen[i] = options[i][o];
            at = prev;
        }
        var total = Math.Min(FashionScore.Cap, chosen.Sum(c => c.Points));
        return new FashionOutfit(chosen, total, total >= target);
    }
}

/// <summary>The score the Fashion Report is done for: 80 points (the full MGP) or 100 (also the "Fashion Leader" title, once).</summary>
public static class FashionTarget
{
    /// <summary>The Title row of "Fashion Leader", given once for 100 points.</summary>
    public const uint LeaderTitle = 141;

    /// <summary>
    /// The score asked for: <paramref name="target"/> (80 or 100), or the older 'set' argument ("easy80", "easy100"), 80 when neither
    /// is given.
    /// </summary>
    public static int Of(int? target, string? set) => target switch
    {
        80 or 100 => target.Value,
        { } other => throw new ArgumentException($"'target' is 80 or 100, not {other}."),
        null => set?.Trim().ToLowerInvariant() switch
        {
            null or "easy80" or "80" => 80,
            "easy100" or "100" => 100,
            _ => throw new ArgumentException($"'set' is easy80 or easy100, not '{set}'; better give 'target' (80 or 100)."),
        },
    };
}

/// <summary>An outfit as lines for the player: what is put on, where it comes from, and what is dyed.</summary>
public static class FashionOutfitText
{
    /// <summary>The gil buying the outfit's pieces costs: vendor prices, and the market listings the plan was made with.</summary>
    public static long PiecesGil(FashionOutfit outfit) =>
        outfit.Choices.Where(c => c.Piece is { Source: PieceSource.Vendor or PieceSource.Market }).Sum(c => c.Piece!.Price ?? 0);

    /// <summary>The line about what the outfit costs; null when it costs nothing.</summary>
    public static string? CostLine(long gil) => gil <= 0 ? null : $"About {gil:N0} gil in all.";

    /// <summary>
    /// One line per slot that changes: a piece not worn yet, or a worn piece that gets dyed. <paramref name="color"/> names the week's
    /// color of a slot.
    /// </summary>
    public static IReadOnlyList<string> Lines(FashionOutfit outfit, Func<int, string?> color)
    {
        var lines = new List<string>();
        foreach (var c in outfit.Choices)
        {
            if (c.Piece is not { } p || p.Source == PieceSource.Worn && c.Dye is not (DyeChange.Dye or DyeChange.Replace)) continue;
            var from = p.Source switch
            {
                PieceSource.Worn => "worn",
                PieceSource.Carried => "from your bags",
                PieceSource.Retainer => $"from {p.Retainer ?? "a retainer"}",
                PieceSource.Armoire => "from the armoire",
                PieceSource.Dresser => "from the glamour dresser",
                PieceSource.Vendor => p.Price is { } v ? $"bought from a vendor, {v:N0} gil" : "bought from a vendor",
                PieceSource.Market => p.Price is { } m ? $"bought on the market board, about {m:N0} gil" : "bought on the market board",
                PieceSource.Duty => "from a dungeon",
                _ => "",
            };
            var dye = c.Dye switch
            {
                DyeChange.Dye => $", dyed {color(c.Slot)}",
                DyeChange.Replace => $", its dye replaced with {color(c.Slot)}",
                DyeChange.AlreadyRight => $", already {color(c.Slot)}",
                _ => "",
            };
            lines.Add($"{GearSlots.Name(c.Slot)}: {p.Name} ({from}){dye}.");
        }
        return lines;
    }
}
