using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace XivMcp.Fashion;

/// <summary>The equipped-items container's slots, and how gear finds its slot from its EquipSlotCategory.</summary>
public static class GearSlots
{
    public const int MainHand = 0, OffHand = 1, Head = 2, Body = 3, Hands = 4, Waist = 5, Legs = 6, Feet = 7, Ears = 8, Neck = 9,
                     Wrists = 10, RingRight = 11, RingLeft = 12, SoulCrystal = 13;

    /// <summary>The slots' names as tools report them, indexed by slot number.</summary>
    public static readonly ImmutableArray<string> Names =
        ["MainHand", "OffHand", "Head", "Body", "Hands", "Waist", "Legs", "Feet", "Ears", "Neck", "Wrists", "RightRing", "LeftRing", "SoulCrystal"];

    /// <summary>A slot's name; a slot past the named ones (the container may grow) shows as its number rather than failing.</summary>
    public static string Name(int slot) => slot >= 0 && slot < Names.Length ? Names[slot] : slot.ToString();

    /// <summary>
    /// The slot an item goes in, from its EquipSlotCategory fields in sheet order (main hand … soul crystal; 1 = goes there, -1 =
    /// blocks it): the first field set to 1, which is the main slot of two-handed weapons and pieces covering several slots. Rings
    /// take the right ring slot unless it is <paramref name="occupied"/>, and never the slot that <paramref name="holds"/> the same
    /// ring already (a second copy replaces the other ring; null when both wear it). Null for items that aren't gear.
    /// </summary>
    public static int? Target(IReadOnlyList<sbyte> category, Func<int, bool>? occupied = null, Func<int, bool>? holds = null)
    {
        // The sheet lists the left ring before the right; the container has the right ring at 11.
        if (IsRing(category))
        {
            var free = new[] { RingRight, RingLeft }.Where(s => holds?.Invoke(s) != true).ToList();
            if (free.Count == 0) return null;
            return free.FirstOrDefault(s => occupied?.Invoke(s) != true, free[0]);
        }
        for (var field = 0; field < category.Count && field < 14; field++)
            if (category[field] == 1) return field switch { 11 => RingLeft, 12 => RingRight, _ => field };
        return null;
    }

    /// <summary>How many copies of an item can be worn at once: two of a ring (one per hand), one of anything else.</summary>
    public static int CopiesWearable(IReadOnlyList<sbyte> category) => IsRing(category) ? 2 : 1;

    private static bool IsRing(IReadOnlyList<sbyte> category) => category.Count > 12 && category[11] == 1 && category[12] == 1;

    /// <summary>A slot as fashion report guides name it ("weapon", "head", …, "ring").</summary>
    public static int FromName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "weapon" or "mainhand" or "main hand" => MainHand,
        "offhand" or "off hand" or "shield" => OffHand,
        "head" => Head,
        "body" => Body,
        "hands" or "gloves" => Hands,
        "legs" => Legs,
        "feet" => Feet,
        "ear" or "ears" or "earrings" => Ears,
        "neck" or "necklace" => Neck,
        "wrist" or "wrists" or "bracelets" => Wrists,
        "ring" or "rings" or "finger" or "right_ring" or "right ring" => RingRight,
        "left_ring" or "left ring" => RingLeft,
        _ => throw new ArgumentException($"'{name}' is not a gear slot."),
    };
}

/// <summary>One piece of a fashion report set: the slot, the item and (if the set dyes it) the dye color.</summary>
public sealed record FashionPiece(string Slot, string Item, string? Dye);

/// <summary>
/// A ready-made set (easy 80 or easy 100 points): its pieces, dyes for slots it doesn't fill, whether the site marks it as this week's,
/// and when the site last updated it (UTC; null when the site does not say).
/// </summary>
public sealed record FashionSet(IReadOnlyList<FashionPiece> Pieces, IReadOnlyList<(string Slot, string Dye)> OtherDyes, bool Fresh, DateTime? PostedUtc = null);

public sealed record FashionHint(string Hint, string Slot);

/// <summary>
/// This week's Fashion Report as fashionreportxiv.com publishes it (its /api/report-state). <see cref="Dyes"/> is the week's exact
/// color per left-side slot ("head" → "Charcoal Grey"), as the site has found them so far.
/// </summary>
public sealed record WeeklyReport(int Week, string Theme, IReadOnlyList<FashionHint> Hints, FashionSet Easy80, FashionSet Easy100, string? Theorycraft,
                                  IReadOnlyDictionary<string, string>? Dyes = null, bool DyesFresh = false)
{

    public static WeeklyReport Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var options = root.GetProperty("lastOptions");
        var hints = options.TryGetProperty("hints", out var h)
            ? h.EnumerateArray().Select(x => new FashionHint(Text(x, "hint"), Text(x, "slot"))).ToList()
            : [];
        return new WeeklyReport(
            int.TryParse(Text(options, "week"), out var week) ? week : 0,
            Text(options, "reportTitle"),
            hints,
            Set(root, "easy80"),
            Set(root, "easy100"),
            root.TryGetProperty("links", out var links) && links.TryGetProperty("theorycraft", out var t) ? t.GetString() : null,
            root.TryGetProperty("dyeData", out var dd) && dd.ValueKind == JsonValueKind.Object
                ? dd.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object)
                    .Select(p => (Slot: p.Name, Dye: DyeName(Text(p.Value, "plus2")))).Where(p => p.Dye is not null)
                    .ToDictionary(p => p.Slot, p => p.Dye!)
                : null,
            root.TryGetProperty("dyesFresh", out var df) && df.ValueKind == JsonValueKind.True);
    }

    /// <summary>The item names fashionreportxiv.com lists for one hint (its /api/hint); empty when it has none yet.</summary>
    public static IReadOnlyList<string> HintItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True || !root.TryGetProperty("items", out var items))
            return [];
        return items.EnumerateArray().Select(i => Text(i, "name")).Where(n => n.Length > 0).Distinct().ToList();
    }

    /// <summary>
    /// The exact dye per slot to aim for: the week's colors when the site has them, otherwise the colors of the set (its pieces' and
    /// the ones for slots it has no piece for). None while the site has this week's hints but has not found the dyes yet
    /// (<paramref name="known"/> false): until then its dyes are the week before's, as the site itself says.
    /// </summary>
    public IReadOnlyDictionary<string, string> WantedDyes(FashionSet set, bool known = true) =>
        !known ? new Dictionary<string, string>()
        : Dyes is { Count: > 0 } week ? week
        : set.Pieces.Where(p => p.Dye is not null).Select(p => (p.Slot, Dye: p.Dye!)).Concat(set.OtherDyes)
              .GroupBy(d => d.Slot).ToDictionary(g => g.Key, g => g.First().Dye);

    private static FashionSet Set(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var set)) return new FashionSet([], [], false);
        var dyes = set.TryGetProperty("dyes", out var d) && d.ValueKind == JsonValueKind.Object
            ? d.EnumerateObject().Select(p => (Slot: p.Name, Dye: DyeName(p.Value.GetString()))).Where(p => p.Dye is not null).ToList()
            : [];
        var pieces = set.TryGetProperty("itemPairs", out var pairs)
            ? pairs.EnumerateArray().Select(p => (Slot: Text(p, "slot"), Item: Text(p, "name"))).Where(p => p.Item.Length > 0)
                   .Select(p => new FashionPiece(p.Slot, p.Item, dyes.FirstOrDefault(x => x.Slot == p.Slot).Dye)).ToList()
            : [];
        var other = dyes.Where(x => pieces.All(p => p.Slot != x.Slot)).Select(x => (x.Slot, x.Dye!)).ToList();
        var fresh = root.TryGetProperty(name + "Fresh", out var f) && f.ValueKind == JsonValueKind.True;
        DateTime? posted = set.TryGetProperty("_updatedAt", out var u) && u.ValueKind == JsonValueKind.Number && u.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;
        return new FashionSet(pieces, other, fresh, posted);
    }

    /// <summary>"Snow White Dye" → "Snow White" (the game's color name); empty → null.</summary>
    private static string? DyeName(string? dye)
    {
        if (string.IsNullOrWhiteSpace(dye)) return null;
        var name = dye.Trim();
        return name.EndsWith(" Dye", StringComparison.OrdinalIgnoreCase) ? name[..^4].Trim() : name;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>Where an item for the Fashion Report could come from.</summary>
public enum ItemSource { None, Have, Retainer, GlamourDresser, Armoire, Vendor, Market, Duty }

/// <summary>How many dyes the Fashion Report job buys, so every dye step finds one in the bags.</summary>
public static class FashionDyes
{
    /// <summary>
    /// The dye items to buy, with how many of each. Each entry of <paramref name="uses"/> is one dye step: the dye items of its color in
    /// the order dye_item tries them (it takes the first one in the bags). Steps are met from <paramref name="owned"/> first; a step
    /// left without one buys the first dye item of its color, which a later step of the same color then also uses.
    /// </summary>
    public static IReadOnlyDictionary<uint, int> ToBuy(IReadOnlyList<IReadOnlyList<uint>> uses, Func<uint, int> owned)
    {
        var left = new Dictionary<uint, int>();
        var buy = new Dictionary<uint, int>();
        foreach (var candidates in uses.Where(c => c.Count > 0))
        {
            foreach (var item in candidates) left.TryAdd(item, owned(item));
            var have = candidates.FirstOrDefault(i => left[i] > 0);
            if (have != 0) left[have]--;
            else buy[candidates[0]] = buy.GetValueOrDefault(candidates[0]) + 1;
        }
        return buy;
    }
}

/// <summary>When the Masked Rose judges: from Friday 08:00 UTC until the weekly reset on Tuesday 08:00 UTC.</summary>
public static class FashionSchedule
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7), ResetToOpening = TimeSpan.FromDays(3);

    /// <summary>The reset that began week 0, a week before the first Fashion Report (patch 4.2), so weeks count as the guides number them.</summary>
    private static readonly DateTime WeekZero = new(2018, 1, 23, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>The Fashion Report week at <paramref name="utc"/>, numbered as fashionreportxiv.com and the weekly guides do.</summary>
    public static int WeekAt(DateTime utc) => (int)((LastReset(utc) - WeekZero).Ticks / Week.Ticks);

    public static bool IsJudgingOpen(DateTime utc) => utc >= LastReset(utc) + ResetToOpening;

    /// <summary>The weekly reset at or before <paramref name="utc"/>: Tuesday 08:00 UTC, when the week's themes change.</summary>
    public static DateTime LastReset(DateTime utc)
    {
        var tuesday = utc.Date.AddDays(-(((int)utc.DayOfWeek - (int)DayOfWeek.Tuesday + 7) % 7)).AddHours(8);
        return tuesday > utc ? tuesday - Week : tuesday;
    }

    /// <summary>This week's judging: from Friday 08:00 UTC after the last reset until the next reset. Both lie ahead before Friday.</summary>
    public static (DateTime Opens, DateTime Closes) JudgingPeriod(DateTime utc)
    {
        var reset = LastReset(utc);
        return (reset + ResetToOpening, reset + Week);
    }

}

/// <summary>
/// The site's guide seen at one moment: its week and the week it is now, whether its theme and hints are this week's, whether the
/// set is, and when judging runs. The site posts a week's hints first, then the dyes, then the sets, so for a while on Friday the
/// hints are this week's and the set is not. A set counts as this week's when the site shows this week and marks the set as
/// posted; a guide kept from before the reset turns old at the reset.
/// </summary>
public sealed record ReportStatus(int Week, int CurrentWeek, bool HintsAreThisWeeks, bool SetIsThisWeeks, DateTime? SetPostedUtc, bool JudgingOpen,
                                  DateTime JudgingOpensUtc, DateTime JudgingClosesUtc)
{
    public static ReportStatus Of(WeeklyReport report, FashionSet set, DateTime utc)
    {
        var (opens, closes) = FashionSchedule.JudgingPeriod(utc);
        var current = FashionSchedule.WeekAt(utc);
        var hints = report.Week == current;
        return new ReportStatus(report.Week, current, hints, hints && set.Fresh, set.PostedUtc, FashionSchedule.IsJudgingOpen(utc), opens, closes);
    }
}

/// <summary>What the player is asked in game before the Fashion Report job starts for a set that is not ready to present.</summary>
public static class FashionApproval
{
    public const string Title = "Start the Fashion Report job anyway?";

    /// <summary>
    /// Why to ask: judging is closed, or the set is not this week's (then also <paramref name="outfit"/>, the lines saying what would be
    /// worn, bought and dyed, so the player sees it). Empty when judging is open and the set is this week's: then nobody is asked.
    /// Without <paramref name="setNeeded"/> (an outfit planned from the hints), only the hints have to be this week's.
    /// </summary>
    public static IReadOnlyList<string> Reasons(ReportStatus status, IReadOnlyList<string> outfit, bool setNeeded = true)
    {
        var reasons = new List<string>();
        if (!status.JudgingOpen)
            reasons.Add($"Judging is closed until {status.JudgingOpensUtc.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)} UTC: " +
                        "the outfit would be prepared, not presented.");
        if (!status.HintsAreThisWeeks || setNeeded && !status.SetIsThisWeeks)
        {
            reasons.Add(!status.HintsAreThisWeeks
                ? $"This outfit is for week {status.Week}, an earlier week. This week is week {status.CurrentWeek}."
                : $"fashionreportxiv.com has this week's hints but has not posted this week's set yet: this one is from an earlier week.");
            reasons.AddRange(outfit);
        }
        return reasons;
    }

    /// <summary>A ready-made set as approval lines: its pieces (with their dyes) and the dyes for slots it has no piece for.</summary>
    public static IReadOnlyList<string> SetLines(FashionSet set)
    {
        var lines = new List<string>();
        if (set.Pieces.Count > 0)
            lines.Add("Pieces: " + string.Join(", ", set.Pieces.Select(p => $"{p.Item} ({p.Slot}{(p.Dye is null ? "" : $", {p.Dye}")})")) + ".");
        if (set.OtherDyes.Count > 0)
            lines.Add("Dyes: " + string.Join(", ", set.OtherDyes.Select(d => $"{d.Slot} {d.Dye}")) + ".");
        return lines;
    }
}

/// <summary>Which pieces bought for the Fashion Report are thrown away after presenting.</summary>
public static class FashionCleanup
{
    /// <summary>
    /// Only pieces the job bought (from a vendor or the market board), and only when they are worth at most <paramref name="maxValue"/>
    /// gil: the vendor price if a vendor sells it, otherwise the market price. A piece of unknown value is kept.
    /// </summary>
    public static bool ShouldDiscard(ItemSource source, int? vendorPrice, int? marketPrice, int maxValue) =>
        source is ItemSource.Vendor or ItemSource.Market && (vendorPrice ?? marketPrice) is { } value && value <= maxValue;
}
