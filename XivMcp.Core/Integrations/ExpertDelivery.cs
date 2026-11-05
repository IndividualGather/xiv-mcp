using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace XivMcp.Integrations;

/// <summary>
/// Grand Company expert delivery through AutoRetainer's own delivery feature: XIV MCP only checks its settings, starts it and reports
/// what left the bags. Its per-character delivery setting decides which items it hands in; only the strictest one is accepted, so
/// nothing from the armoury chest or a gear set is ever handed in.
/// </summary>
public static class ExpertDelivery
{
    /// <summary>AutoRetainer's GCDeliveryType: Disabled, Hide_Armoury_Chest_Items, Hide_Gear_Set_Items, Show_All_Items.</summary>
    private const int HideArmouryChestItems = 1;

    /// <summary>Why delivering is refused for this character with this AutoRetainer config (DefaultConfig.json), or null when it may go ahead.</summary>
    public static string? Refusal(string autoRetainerConfig, ulong contentId)
    {
        using var doc = JsonDocument.Parse(autoRetainerConfig);
        if (!doc.RootElement.TryGetProperty("OfflineData", out var characters) || characters.ValueKind != JsonValueKind.Array)
            return "AutoRetainer's settings have no characters yet; log in once with AutoRetainer loaded.";
        var me = characters.EnumerateArray().FirstOrDefault(c => c.TryGetProperty("CID", out var cid) && cid.TryGetUInt64(out var id) && id == contentId);
        if (me.ValueKind != JsonValueKind.Object)
            return "AutoRetainer doesn't know this character yet; log in once with AutoRetainer loaded.";
        var type = me.TryGetProperty("GCDeliveryType", out var t) && t.TryGetInt32(out var v) ? v : 0;
        return type switch
        {
            HideArmouryChestItems => null,
            0 => "AutoRetainer's Grand Company delivery is turned off for this character. Set it to \"Hide Armoury Chest Items\" in AutoRetainer.",
            2 => "AutoRetainer would hand in armoury chest items for this character (\"Hide Gear Set Items\"); only \"Hide Armoury Chest Items\" is allowed, so gear set and armoury items stay.",
            _ => "AutoRetainer would hand in items from the armoury chest for this character (\"Show All Items\"); only \"Hide Armoury Chest Items\" is allowed.",
        };
    }

    /// <summary>What AutoRetainer buys with the seals (its default exchange plan), and when.</summary>
    public sealed record ExchangePlan(IReadOnlyList<uint> Buys, bool BuysAfterwards, bool ContinuesWhenCapped);

    public static ExchangePlan Plan(string autoRetainerConfig)
    {
        using var doc = JsonDocument.Parse(autoRetainerConfig);
        var root = doc.RootElement;
        var buys = new List<uint>();
        var after = false;
        if (root.TryGetProperty("DefaultGCExchangePlan", out var plan) && plan.ValueKind == JsonValueKind.Object)
        {
            if (plan.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var i in items.EnumerateArray())
                    if (i.TryGetProperty("ItemID", out var id) && id.TryGetUInt32(out var itemId) &&
                        ((i.TryGetProperty("Quantity", out var q) && q.TryGetInt32(out var n) && n > 0) ||
                         (i.TryGetProperty("QuantitySingleTime", out var s) && s.TryGetInt32(out var once) && once > 0)))
                        buys.Add(itemId);
            after = plan.TryGetProperty("FinalizeByPurchasing", out var f) && f.ValueKind == JsonValueKind.True;
        }
        var continues = root.TryGetProperty("AutoGCContinuation", out var c) && c.ValueKind == JsonValueKind.True;
        return new ExchangePlan(buys, after, continues);
    }

    /// <summary>Items (and how many) that are no longer in the bags: what was handed in.</summary>
    public static Dictionary<uint, int> Gone(IReadOnlyDictionary<uint, int> before, IReadOnlyDictionary<uint, int> after) =>
        before.Select(b => (b.Key, Left: b.Value - (after.TryGetValue(b.Key, out var a) ? a : 0)))
              .Where(x => x.Left > 0)
              .ToDictionary(x => x.Key, x => x.Left);
}
