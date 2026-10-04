using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using XivMcp.Maps;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Moving without navigation plugins: XIV MCP shows the player the way and waits until they are there. Walking puts the flag on
/// the map; teleporting names the aetheryte (and aethernet shard) and opens the map at it; houses and inn rooms are named. The
/// waits end when the player arrives, or when the tool is stopped or times out.
/// </summary>
internal static partial class NavigationTools
{
    /// <summary>Flags a spot in a zone, asks the player to walk there and waits until they are within range (or <paramref name="arrived"/>).</summary>
    internal static async Task GuideWalk(uint territory, Vector3 position, string label, float range, Func<bool>? arrived, List<string> steps,
                                         CancellationToken ct)
    {
        var message = await Game.Run(() =>
        {
            var map = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Map.ValueNullable
                      ?? throw new XivMcp.Mcp.ToolException($"XIV MCP does not know the map of {TerritoryName(territory)}.");
            SetFlag(territory, map.RowId, position, label);
            var (x, y) = WorldToMap(map, position);
            return PlayerGuidance.WalkTo(label, TerritoryName(territory), x, y);
        }).ConfigureAwait(false);
        PlayerGuide.Ask(message);
        steps.Add($"Asked you to walk to {label} (flag on the map).");
        phase = $"waiting for you to walk to {label}";
        try
        {
            await WaitFor(() => (arrived?.Invoke() ?? false) ||
                                (Svc.ClientState.TerritoryType == territory && Svc.Objects.LocalPlayer is { } p &&
                                 MapMath.GroundDistance(p.Position.X, p.Position.Z, position.X, position.Z) <= range), ct).ConfigureAwait(false);
            steps.Add($"You reached {label}.");
        }
        finally
        {
            await Game.Run(() => { ClearFlag(); return true; }).ConfigureAwait(false);
            PlayerGuide.Done();
        }
    }

    /// <summary>Asks the player to teleport (to a shard: to its city's aetheryte, then by aethernet) and waits until they are in the zone.</summary>
    internal static async Task GuideTeleport(uint aetheryteId, bool shard, uint territory, List<string> steps, CancellationToken ct)
    {
        var (message, label) = await Game.Run(() =>
        {
            var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
            var target = sheet.GetRow(aetheryteId);
            // A shard is reached through its city's main aetheryte.
            var main = shard && target.AethernetGroup != 0 && sheet.FirstOrDefault(a => a.IsAetheryte && a.AethernetGroup == target.AethernetGroup) is { RowId: not 0 } city
                ? city : target;
            shard = shard && main.RowId != target.RowId;
            var mainName = Excel.Name(main.PlaceName) ?? $"aetheryte {main.RowId}";
            var shardName = shard ? Excel.Name(target.AethernetName) : null;
            if (main.Map.ValueNullable is { } map && AetherytePosition(main) is { } pos)
            {
                SetFlag(main.Territory.RowId, map.RowId, pos, mainName);
            }
            return (PlayerGuidance.TeleportTo(mainName, TerritoryName(territory), shardName), shardName ?? mainName);
        }).ConfigureAwait(false);
        PlayerGuide.Ask(message);
        steps.Add($"Asked you to teleport ({label}).");
        phase = $"waiting for you to travel to {TerritoryName(territory)}";
        try
        {
            await WaitFor(() => Svc.ClientState.TerritoryType == territory, ct).ConfigureAwait(false);
            await WaitUntilSettled(ct).ConfigureAwait(false);
            steps.Add($"You arrived in {await Game.Run(ZoneName).ConfigureAwait(false)}.");
        }
        finally
        {
            await Game.Run(() => { ClearFlag(); return true; }).ConfigureAwait(false);
            PlayerGuide.Done();
        }
    }

    /// <summary>Asks the player to go to an inn room or into a house, apartment or workshop, and waits until they are in one.</summary>
    internal static async Task GuidePlace(string destination, List<string> steps, CancellationToken ct)
    {
        PlayerGuide.Ask(PlayerGuidance.GoTo(destination));
        steps.Add($"Asked you to go to {(destination is "inn" or "lifestream" ? "an inn room" : destination == "fc" ? "your free company house" : $"your {destination}")}.");
        phase = $"waiting for you to go to {destination}";
        try
        {
            await WaitFor(() => PlayerGuidance.Reached(destination, CurrentIntendedUse()), ct).ConfigureAwait(false);
            await WaitUntilSettled(ct).ConfigureAwait(false);
            steps.Add($"You arrived in {await Game.Run(ZoneName).ConfigureAwait(false)}.");
        }
        finally { PlayerGuide.Done(); }
    }

    private static uint CurrentIntendedUse() =>
        Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(Svc.ClientState.TerritoryType)?.TerritoryIntendedUse.RowId ?? 0;

    /// <summary>Polls a condition on the framework thread until it holds, between zone loads.</summary>
    private static async Task WaitFor(Func<bool> condition, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await Game.Run(() => !Svc.Condition[ConditionFlag.BetweenAreas] && Svc.Objects.LocalPlayer is not null && condition()).ConfigureAwait(false))
                return;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private static unsafe void SetFlag(uint territory, uint mapId, Vector3 position, string label)
    {
        var agent = AgentMap.Instance();
        agent->SetFlagMapMarker(territory, mapId, position);
        agent->OpenMap(mapId, territory, label);
    }

    private static unsafe void ClearFlag() => AgentMap.Instance()->FlagMarkerCount = 0;
}
