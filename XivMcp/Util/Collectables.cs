using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace XivMcp.Util;

/// <summary>Collectable turn-in data: collectability tiers, the scrip each tier pays, and how much more of that scrip fits under its cap.</summary>
internal static class Collectables
{
    public sealed record Reward(uint ItemId, uint CurrencyItemId, int LowCollectability, int MidCollectability, int HighCollectability,
                                int LowReward, int MidReward, int HighReward);

    /// <summary>Items that are always collectable: each one takes its own bag slot (they never stack).</summary>
    public static bool IsCollectable(uint itemId) => Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.AlwaysCollectable == true;

    /// <summary>What an appraiser pays for the item, or null if appraisers don't take it. Framework thread (reads the currency manager).</summary>
    public static unsafe Reward? RewardFor(uint itemId)
    {
        foreach (var group in Svc.Data.GetSubrowExcelSheet<CollectablesShopItem>())
            foreach (var row in group)
            {
                if (row.Item.RowId != itemId) continue;
                if (row.CollectablesShopRewardScrip.ValueNullable is not { } scrip || row.CollectablesShopRefine.ValueNullable is not { } refine) continue;
                var currency = CurrencyManager.Instance() is var cm && cm != null ? cm->GetItemIdBySpecialId((byte)scrip.Currency) : 0;
                return new Reward(itemId, currency, refine.LowCollectability, refine.MidCollectability, refine.HighCollectability,
                                  scrip.LowReward, scrip.MidReward, scrip.HighReward);
            }
        return null;
    }

    /// <summary>Current amount, cap and room under the cap for a currency. Framework thread.</summary>
    public static unsafe (long Have, long Max, long Room) CurrencyRoom(uint currencyItemId)
    {
        var cm = CurrencyManager.Instance();
        if (cm == null || currencyItemId == 0) return (0, 0, long.MaxValue);
        long have = cm->GetItemCount(currencyItemId);
        long max = cm->GetItemMaxCount(currencyItemId);
        return (have, max, max > 0 ? Math.Max(0, max - have) : long.MaxValue);
    }

    public static object Describe(Reward r)
    {
        var room = CurrencyRoom(r.CurrencyItemId);
        return new
        {
            currency = Items.Name(r.CurrencyItemId),
            tiers = new[]
            {
                new { collectability = r.LowCollectability, scrip = r.LowReward },
                new { collectability = r.MidCollectability, scrip = r.MidReward },
                new { collectability = r.HighCollectability, scrip = r.HighReward },
            }.Where(t => t.collectability > 0).ToList(),
            have = room.Have,
            cap = room.Max,
            turnInsBeforeCap = r.HighReward > 0 && room.Room != long.MaxValue ? room.Room / r.HighReward : (long?)null,
        };
    }
}
