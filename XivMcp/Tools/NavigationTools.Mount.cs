using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using XivMcp.Maps;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Riding instead of walking: Mount Roulette for longer walks (setting in /xivmcp), flying where the zone allows it.</summary>
internal static partial class NavigationTools
{
    private const uint MountRoulette = 9, Dismount = 23; // GeneralAction rows

    /// <summary>
    /// Starts a vnavmesh path to <paramref name="target"/>; mounts up first for a longer walk and flies where the zone allows it.
    /// Returns whether vnavmesh started the path.
    /// </summary>
    internal static async Task<bool> StartPath(Vector3 target, float range, List<string> steps, CancellationToken ct)
    {
        var enabled = Plugin.Instance?.Config.UseMountForWalks ?? true;
        var choice = await Game.Run(() => Mounting.Choose(Game.DistanceToPlayer(target) ?? 0, enabled, Mounted, CanMount(), CanFly())).ConfigureAwait(false);
        if (choice == MountChoice.Mount)
        {
            await Game.Run(() => { unsafe { return ActionManager.Instance()->UseAction(ActionType.GeneralAction, MountRoulette); } }).ConfigureAwait(false);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!await Game.Run(() => Mounted).ConfigureAwait(false) && DateTime.UtcNow < until) await Task.Delay(200, ct).ConfigureAwait(false);
            if (await Game.Run(() => Mounted).ConfigureAwait(false)) steps.Add("Mounted up.");
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        var fly = await Game.Run(() => Mounting.Fly(Mounted, CanFly())).ConfigureAwait(false);
        if (fly) steps.Add("Flying.");
        return await Game.Run(() => Navigation.MoveCloseTo(target, range, fly)).ConfigureAwait(false);
    }

    /// <summary>Gets off the mount (landing first when flying), so the player can talk, fish or use things.</summary>
    internal static async Task GetOffMount(List<string> steps, CancellationToken ct)
    {
        if (!await Game.Run(() => Mounted).ConfigureAwait(false)) return;
        var until = DateTime.UtcNow.AddSeconds(8);
        while (await Game.Run(() => Mounted).ConfigureAwait(false) && DateTime.UtcNow < until)
        {
            // While flying, the first use lands and the next one gets off.
            await Game.Run(() => { unsafe { return ActionManager.Instance()->UseAction(ActionType.GeneralAction, Dismount); } }).ConfigureAwait(false);
            await Task.Delay(700, ct).ConfigureAwait(false);
        }
        steps.Add("Got off the mount.");
    }

    private static bool Mounted => Svc.Condition[ConditionFlag.Mounted];

    private static unsafe bool CanMount() => ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, MountRoulette) == 0;

    /// <summary>The zone allows flying and the player has all its aether currents.</summary>
    private static unsafe bool CanFly()
    {
        if (Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(Svc.ClientState.TerritoryType) is not { } t) return false;
        var set = t.AetherCurrentCompFlgSet.RowId;
        return set != 0 && PlayerState.Instance()->IsAetherCurrentZoneComplete(set);
    }
}
