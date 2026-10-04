using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace XivMcp.Fashion;

/// <summary>The equipped-items container's slots, and how gear finds its slot from its EquipSlotCategory.</summary>
public static class GearSlots
{
    public const int MainHand = 0, OffHand = 1, Head = 2, Body = 3, Hands = 4, Waist = 5, Legs = 6, Feet = 7, Ears = 8, Neck = 9,
                     Wrists = 10, RingRight = 11, RingLeft = 12, SoulCrystal = 13;

    /// <summary>
    /// The slot an item goes in, from its EquipSlotCategory fields in sheet order (main hand … soul crystal; 1 = goes there, -1 =
    /// blocks it): the first field set to 1, which is the main slot of two-handed weapons and pieces covering several slots. Rings
    /// take the right ring slot unless it is <paramref name="occupied"/>. Null for items that aren't gear.
    /// </summary>
    public static int? Target(IReadOnlyList<sbyte> category, Func<int, bool>? occupied = null)
    {
        // The sheet lists the left ring before the right; the container has the right ring at 11.
        if (category.Count > 12 && category[11] == 1 && category[12] == 1)
            return occupied?.Invoke(RingRight) == true ? RingLeft : RingRight;
        for (var field = 0; field < category.Count && field < 14; field++)
            if (category[field] == 1) return field switch { 11 => RingLeft, 12 => RingRight, _ => field };
        return null;
    }

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
        "ring" or "rings" or "finger" => RingRight,
        _ => throw new ArgumentException($"'{name}' is not a gear slot."),
    };
}

/// <summary>One piece of a fashion report set: the slot, the item and (if the set dyes it) the dye color.</summary>
public sealed record FashionPiece(string Slot, string Item, string? Dye);

/// <summary>A ready-made set (easy 80 or easy 100 points): its pieces, dyes for slots it doesn't fill, and whether it is this week's.</summary>
public sealed record FashionSet(IReadOnlyList<FashionPiece> Pieces, IReadOnlyList<(string Slot, string Dye)> OtherDyes, bool Fresh);

public sealed record FashionHint(string Hint, string Slot);

/// <summary>This week's Fashion Report as fashionreportxiv.com publishes it (its /api/report-state).</summary>
public sealed record WeeklyReport(int Week, string Theme, IReadOnlyList<FashionHint> Hints, FashionSet Easy80, FashionSet Easy100, string? Theorycraft)
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
            root.TryGetProperty("links", out var links) && links.TryGetProperty("theorycraft", out var t) ? t.GetString() : null);
    }

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
        return new FashionSet(pieces, other, fresh);
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
public enum ItemSource { None, Have, Retainer, GlamourDresser, Armoire, Vendor, Duty }

/// <summary>Where a player has an item, and whether it can be bought from a vendor or dropped in a duty.</summary>
public sealed record ItemWhereabouts(bool InBags, string? Retainer, bool InGlamourDresser, bool InArmoire, bool AtVendor, bool FromDuty);

public static class FashionPlan
{
    /// <summary>
    /// The cheapest way to the item: carried already, then stored (retainer, glamour dresser, armoire), then bought from a vendor,
    /// and a dungeon last because it takes longest.
    /// </summary>
    public static ItemSource Choose(ItemWhereabouts w) =>
        w.InBags ? ItemSource.Have
        : w.Retainer is not null ? ItemSource.Retainer
        : w.InGlamourDresser ? ItemSource.GlamourDresser
        : w.InArmoire ? ItemSource.Armoire
        : w.AtVendor ? ItemSource.Vendor
        : w.FromDuty ? ItemSource.Duty
        : ItemSource.None;
}

/// <summary>When the Masked Rose judges: from Friday 08:00 UTC until the weekly reset on Tuesday 08:00 UTC.</summary>
public static class FashionSchedule
{
    public static bool IsJudgingOpen(DateTime utc)
    {
        var hoursSinceMonday = ((int)utc.DayOfWeek + 6) % 7 * 24 + utc.Hour; // Monday 00:00 = 0
        const int tuesdayReset = 24 + 8, fridayOpen = 4 * 24 + 8;
        return hoursSinceMonday >= fridayOpen || hoursSinceMonday < tuesdayReset;
    }
}
