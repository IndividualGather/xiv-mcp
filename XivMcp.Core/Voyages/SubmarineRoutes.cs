using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XivMcp.Voyages;

/// <summary>
/// A point a submersible can explore (the SubmarineExploration sheet): its sea (SubmarineMap), name, the letter the voyage map shows
/// for it, the rank it needs, and whether the free company has unlocked it.
/// </summary>
public sealed record SubmarinePoint(uint Id, uint Map, string Name, string Code, int RankRequired, bool Unlocked);

public static class SubmarineRoutes
{
    /// <summary>A voyage visits at most five points.</summary>
    public const int MaxPoints = 5;

    /// <summary>
    /// The route for points given by name, letter or id, in the order given. Letters (A, B, …, which every sea uses again) are read
    /// on the sea of the points named otherwise, else on <paramref name="vesselSea"/> (the sea of the submersible's last voyage);
    /// a letter that several seas have is refused when neither tells the sea. Throws <see cref="FormatException"/> for a route the
    /// game would not take.
    /// </summary>
    public static IReadOnlyList<SubmarinePoint> Resolve(IReadOnlyList<string> wanted, IReadOnlyList<SubmarinePoint> all, int vesselRank, uint? vesselSea = null)
    {
        if (wanted.Count == 0) throw new FormatException("A route needs at least one point.");
        if (wanted.Count > MaxPoints) throw new FormatException($"A voyage visits at most {MaxPoints} points, not {wanted.Count}.");

        // The sea, from the points named in full or by id, else the submersible's own.
        var seas = wanted.Select(w => ByNameOrId(w.Trim(), all)).Where(p => p is not null).Select(p => p!.Map).Distinct().ToList();
        if (seas.Count > 1) throw new FormatException("All points of a voyage must be on one sea.");
        uint? sea = seas.Count == 1 ? seas[0] : vesselSea;
        if (sea is null)
        {
            var letterSeas = wanted.SelectMany(w => all.Where(p => p.Code.Equals(w.Trim(), StringComparison.OrdinalIgnoreCase)).Select(p => p.Map)).Distinct().ToList();
            if (letterSeas.Count > 1)
                throw new FormatException("Those letters are on several seas: name the points (e.g. \"Crow's Drop\") or add one by name, so the sea is clear.");
            sea = letterSeas.FirstOrDefault();
        }

        var route = new List<SubmarinePoint>();
        foreach (var raw in wanted)
        {
            var w = raw.Trim();
            var point = ByNameOrId(w, all) ?? all.FirstOrDefault(p => p.Map == sea && p.Code.Equals(w, StringComparison.OrdinalIgnoreCase))
                        ?? throw new FormatException($"No voyage point '{w}'.");
            if (point.Map != sea) throw new FormatException("All points of a voyage must be on one sea.");
            if (!point.Unlocked) throw new FormatException($"{point.Name} has not been unlocked yet.");
            if (point.RankRequired > vesselRank) throw new FormatException($"{point.Name} needs rank {point.RankRequired}; the submersible is rank {vesselRank}.");
            if (route.Contains(point)) throw new FormatException($"{point.Name} is in the route twice.");
            route.Add(point);
        }
        return route;
    }

    private static SubmarinePoint? ByNameOrId(string w, IReadOnlyList<SubmarinePoint> all) =>
        uint.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? all.FirstOrDefault(p => p.Id == id)
        : all.FirstOrDefault(p => SameName(p.Name, w));

    /// <summary>
    /// Names match in any case, whatever apostrophe either side uses, with or without "the", and with or without the map letter the
    /// game adds ("Crow's Drop (G)").
    /// </summary>
    private static bool SameName(string a, string b)
    {
        static string Norm(string s)
        {
            var t = s.Replace('’', '\'').Replace('‘', '\'').Trim();
            if (t.EndsWith(')') && t.LastIndexOf(" (", StringComparison.Ordinal) is var open and > 0 && t.Length - open <= 6) t = t[..open].TrimEnd();
            return t.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? t[4..] : t;
        }
        return Norm(a).Equals(Norm(b), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Part condition, which parts to repair, and the "used/available" numbers of the voyage window.</summary>
public static class VesselRepair
{
    /// <summary>The game keeps a part's condition as 0–30,000 for 0–100 %.</summary>
    public static int Percent(int condition) => Math.Clamp(condition / 300, 0, 100);

    /// <summary>The part slots (0 hull, 1 stern, 2 bow, 3 bridge) whose condition is below <paramref name="belowPercent"/>.</summary>
    public static IReadOnlyList<int> SlotsToRepair(IReadOnlyList<int> percents, int belowPercent) =>
        Enumerable.Range(0, percents.Count).Where(i => percents[i] < belowPercent).ToList();

    /// <summary>"12/345" as the voyage details show fuel and distance: used, available.</summary>
    public static (int Used, int Available)? Fraction(string text)
    {
        var parts = text.Split('/');
        return parts.Length == 2 && int.TryParse(parts[0].Trim(), out var used) && int.TryParse(parts[1].Trim(), out var available)
            ? (used, available) : null;
    }
}
