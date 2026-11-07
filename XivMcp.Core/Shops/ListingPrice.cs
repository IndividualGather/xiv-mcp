namespace XivMcp.Shops;

/// <summary>The market board's limit on a listing: its total (price per unit times quantity) is at most 999,999,999 gil.</summary>
public static class ListingPrice
{
    public const long MaxTotal = 999_999_999;

    /// <summary>The highest price per unit a listing of this many can have.</summary>
    public static int MaxUnitPrice(int quantity) => (int)(MaxTotal / System.Math.Max(1, quantity));

    /// <summary>Why the game would not take this price, or null.</summary>
    public static string? Problem(int pricePerUnit, int quantity) =>
        (long)pricePerUnit * quantity > MaxTotal
            ? $"A listing totals at most {MaxTotal:N0} gil: for {quantity:N0} the price can be at most {MaxUnitPrice(quantity):N0} per unit."
            : null;
}
