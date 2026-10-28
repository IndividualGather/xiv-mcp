using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.CustomDeliveries;
using GameAchievement = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement;

namespace XivMcp.Util;

/// <summary>
/// Progress of counting achievements, which the game asks the server for one at a time (as the Achievements window does when one is
/// selected): the request, then the answer in the achievement's progress fields.
/// </summary>
internal static class AchievementProgress
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static List<DeliveryAchievement>? deliveryAchievements;

    /// <summary>The current and needed count of an achievement, or null when the server did not answer in time.</summary>
    public static async Task<(uint Current, uint Max)?> Read(uint id, CancellationToken ct)
    {
        if (!await Game.Run(() => Request(id)).ConfigureAwait(false)) return null;
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            if (await Game.Run(() => Received(id)).ConfigureAwait(false) is { } progress) return progress;
        }
        return null;
    }

    /// <summary>How many deliveries a custom delivery client's achievements still need, or null when unknown.</summary>
    public static async Task<int?> DeliveriesLeft(string client, CancellationToken ct)
    {
        deliveryAchievements ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Achievement>()
            .Where(a => a.Data.Count > 0)
            .Select(a => new DeliveryAchievement(a.RowId, a.Name.ExtractText(), (int)a.Data[0].RowId))
            .Where(a => a.Name.Length > 0).ToList();
        if (DeliveryAchievement.LastTier(client, deliveryAchievements) is not { } last) return null;
        return await Read(last.Id, ct).ConfigureAwait(false) is var (current, max) ? DeliveryAchievement.Left(current, max) : null;
    }

    private static unsafe bool Request(uint id)
    {
        var a = GameAchievement.Instance();
        if (a == null) return false;
        a->RequestAchievementProgress(id);
        return true;
    }

    private static unsafe (uint, uint)? Received(uint id)
    {
        var a = GameAchievement.Instance();
        if (a == null || a->ProgressRequestState != GameAchievement.AchievementState.Loaded || a->ProgressAchievementId != id) return null;
        return (a->ProgressCurrent, a->ProgressMax);
    }
}
