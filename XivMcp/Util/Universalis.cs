using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Market prices from universalis.app (the crowd-sourced market board data Price Insight and others use): current listings, recent
/// sales, averages and sale velocity per world, data center or region. Rate-limited politely (one request at a time, max 100 items each).
/// </summary>
internal static class Universalis
{
    private const string Api = "https://universalis.app/api/v2";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public sealed record Listing(long PricePerUnit, int Quantity, bool Hq, string? Retainer, string? World, DateTime? Reviewed);

    public sealed record Sale(long PricePerUnit, int Quantity, bool Hq, string? Buyer, string? World, DateTime When);

    public sealed record ItemMarket(uint ItemId, string Scope, List<Listing> Listings, List<Sale> RecentSales, double? AveragePrice,
                                    double? AveragePriceNq, double? AveragePriceHq, double? SalesPerDay, DateTime? LastUpload, int ListingCount);

    /// <summary>Market data for items in a world, data center or region name.</summary>
    public static async Task<Dictionary<uint, ItemMarket>> Get(IReadOnlyCollection<uint> itemIds, string scope, int listings, int entries, CancellationToken ct)
    {
        var result = new Dictionary<uint, ItemMarket>();
        foreach (var chunk in itemIds.Distinct().Chunk(100))
        {
            var url = $"{Api}/{Uri.EscapeDataString(scope)}/{string.Join(',', chunk)}?listings={listings}&entries={entries}";
            JsonNode? json;
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var response = await Teamcraft.Http.GetAsync(url, ct).ConfigureAwait(false);
                if ((int)response.StatusCode == 404) continue; // untradeable / unknown items
                response.EnsureSuccessStatusCode();
                json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            catch (HttpRequestException ex)
            {
                throw new ToolException($"Universalis did not answer: {ex.Message}");
            }
            finally
            {
                Gate.Release();
            }
            // One item → the item object itself; several → { items: { id: {...} } }.
            var items = chunk.Length == 1 ? [json] : (json?["items"] as JsonObject)?.Select(kv => kv.Value).ToList() ?? [];
            foreach (var item in items.OfType<JsonObject>())
            {
                var id = (uint)(item["itemID"]?.GetValue<long>() ?? 0);
                if (id == 0) continue;
                result[id] = new ItemMarket(
                    id, scope,
                    (item["listings"] as JsonArray ?? []).OfType<JsonObject>().Select(l => new Listing(
                        l["pricePerUnit"]?.GetValue<long>() ?? 0, l["quantity"]?.GetValue<int>() ?? 0, l["hq"]?.GetValue<bool>() ?? false,
                        l["retainerName"]?.ToString(), l["worldName"]?.ToString() ?? item["worldName"]?.ToString(),
                        l["lastReviewTime"] is { } t ? DateTimeOffset.FromUnixTimeSeconds(t.GetValue<long>()).UtcDateTime : null)).ToList(),
                    (item["recentHistory"] as JsonArray ?? []).OfType<JsonObject>().Select(s => new Sale(
                        s["pricePerUnit"]?.GetValue<long>() ?? 0, s["quantity"]?.GetValue<int>() ?? 0, s["hq"]?.GetValue<bool>() ?? false,
                        s["buyerName"]?.ToString(), s["worldName"]?.ToString() ?? item["worldName"]?.ToString(),
                        DateTimeOffset.FromUnixTimeSeconds(s["timestamp"]?.GetValue<long>() ?? 0).UtcDateTime)).ToList(),
                    Num(item["averagePrice"]), Num(item["averagePriceNQ"]), Num(item["averagePriceHQ"]), Num(item["regularSaleVelocity"]),
                    item["lastUploadTime"] is { } up ? DateTimeOffset.FromUnixTimeMilliseconds(up.GetValue<long>()).UtcDateTime : null,
                    item["listingsCount"]?.GetValue<int>() ?? 0);
            }
        }
        return result;
    }

    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && d > 0 ? Math.Round(d, 1) : null;

    /// <summary>The player's home world name and its data center / region (Universalis scopes). Framework thread.</summary>
    public static (string World, string DataCenter, string Region) HomeScopes()
    {
        var world = Svc.PlayerState.HomeWorld.ValueNullable ?? throw new ToolException("Home world unknown; log in first.");
        var dc = world.DataCenter.ValueNullable;
        var region = dc?.Region.RowId switch { 1 => "Japan", 2 => "North-America", 3 => "Europe", 4 => "Oceania", 5 => "China", 6 => "Korea", _ => "Europe" };
        return (world.Name.ExtractText(), dc?.Name.ExtractText() ?? world.Name.ExtractText(), region);
    }
}
