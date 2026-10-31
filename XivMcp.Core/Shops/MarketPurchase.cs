namespace XivMcp.Shops;

/// <summary>What a market board purchase that fell short says, so a job stops instead of going on as if all were bought.</summary>
public static class MarketPurchase
{
    /// <summary>
    /// Null when at least <paramref name="wanted"/> were bought. Otherwise what was bought and why the rest wasn't:
    /// <paramref name="nextPrice"/> is the cheapest listing left (above the limit), null when nothing else is listed.
    /// </summary>
    public static string? Shortfall(string name, int wanted, int got, long spent, uint? nextPrice, int maxPrice)
    {
        if (got >= wanted) return null;
        var why = nextPrice is { } price
            ? $"the next listing costs {price:N0} gil each, above your limit of {maxPrice:N0}"
            : "nobody sells more on this world right now";
        var missing = wanted - got;
        return $"Bought only {got} of {wanted} {name} ({spent:N0} gil): {why}. {missing} {(missing == 1 ? "is" : "are")} still missing.";
    }
}
