using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Fishing;

/// <summary>
/// Bite times players reported to FFXIV Teamcraft (its public GraphQL API at gubal.ffxivteamcraft.com): per fish, spot and bait,
/// how many bites came after each whole second.
/// </summary>
public static class TeamcraftBiteTimes
{
    public const string Endpoint = "https://gubal.ffxivteamcraft.com/graphql";

    /// <summary>The request body for every fish that bites at a spot with a bait.</summary>
    public static string Query(uint spot, uint bait) => new JsonObject
    {
        ["query"] = $"{{ bite_time_per_fish_per_spot_per_bait(where: {{spot: {{_eq: {spot}}}, baitId: {{_eq: {bait}}}}}) " +
                    "{ itemId flooredBiteTime occurences } }",
    }.ToJsonString();

    /// <summary>A bite window per fish from the answer; fish with too few reports are left out.</summary>
    public static IReadOnlyDictionary<uint, BiteWindow> Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return new Dictionary<uint, BiteWindow>(); }
        if (root?["data"]?["bite_time_per_fish_per_spot_per_bait"] is not JsonArray rows) return new Dictionary<uint, BiteWindow>();
        return rows.OfType<JsonObject>()
            .Select(r => (Fish: FishTrackerData.Num(r["itemId"]), Seconds: FishTrackerData.Num(r["flooredBiteTime"]), Count: FishTrackerData.Num(r["occurences"])))
            .Where(r => r.Fish is not null && r.Seconds is not null && r.Count is not null)
            .GroupBy(r => (uint)r.Fish!.Value)
            .Select(g => (g.Key, Window: BiteTimes.Summarize(g.Select(r => ((int)r.Seconds!.Value, (int)r.Count!.Value)))))
            .Where(x => x.Window is not null)
            .ToDictionary(x => x.Key, x => x.Window!);
    }
}
