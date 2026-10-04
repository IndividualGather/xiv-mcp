using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;
using static XivMcp.Util.Navigation;

namespace XivMcp.Tools;

/// <summary>Travelling to a fishing spot: into its zone, then to the water's edge, until Cast can be used.</summary>
internal static partial class NavigationTools
{
    internal const uint CastAction = 289, QuitAction = 299;
    internal const uint FisherJob = 18;
    private const float SpotReach = 30f;

    /// <summary>A fishing spot's zone, its centre in the world, how far it reaches (yalms) and its name.</summary>
    internal sealed record FishingPlace(uint Spot, string Name, uint Territory, uint Map, Vector3 Centre, float Radius);

    /// <summary>Whether the character can cast its line right now: a Fisher at a fishing spot's water. Framework thread.</summary>
    internal static unsafe bool CanCast() =>
        Svc.Objects.LocalPlayer is { } p && p.ClassJob.RowId == FisherJob && ActionManager.Instance()->GetActionStatus(ActionType.Action, CastAction) == 0;

    /// <summary>
    /// Where a fishing spot is. The map coordinates come from Teamcraft's fishing data when it is loaded; otherwise from the
    /// FishingSpot sheet, whose X and Z are map marker positions (0–2048 on the map) like MapMarker's.
    /// </summary>
    internal static FishingPlace? FindFishingSpot(uint spot)
    {
        if (Svc.Data.GetExcelSheet<FishingSpot>().GetRowOrDefault(spot) is not { } row || row.TerritoryType.RowId == 0) return null;
        if (row.TerritoryType.ValueNullable?.Map.ValueNullable is not { } map) return null;
        var scale = map.SizeFactor / 100f;
        Vector3 centre;
        if (FishingSources.Cached?.Spots.GetValueOrDefault(spot) is { MapX: > 0 } info) centre = MapToWorld(map, info.MapX, info.MapY);
        else centre = new Vector3((row.X - 1024) / scale - map.OffsetX, 0, (row.Z - 1024) / scale - map.OffsetY);
        // Arriving means being able to cast; this distance only matters before switching to Fisher.
        return new FishingPlace(spot, FishingTools.SpotName(spot), row.TerritoryType.RowId, map.RowId, centre, SpotReach);
    }

    /// <summary>Travels to a fishing spot and walks to its water until Cast can be used (as a Fisher), or to its edge otherwise.</summary>
    internal static async Task<object> GoToFishingSpot(FishingPlace place, List<string> steps, CancellationToken ct)
    {
        await Game.RunLoggedIn(() => { EnsureCanTravel(); return true; }).ConfigureAwait(false);
        bool Here() => Svc.ClientState.TerritoryType == place.Territory && Svc.Objects.LocalPlayer is { } p &&
                       (CanCast() || Vector2.Distance(new(p.Position.X, p.Position.Z), new(place.Centre.X, place.Centre.Z)) <= place.Radius);

        if (!await Game.Run(Here).ConfigureAwait(false))
        {
            await TravelNear(new NpcSpot(0, place.Name, place.Territory, place.Map, place.Centre, false), steps, ct).ConfigureAwait(false);
            if (!VnavmeshLoaded)
                await GuideWalk(place.Territory, place.Centre, place.Name, place.Radius, Here, steps, ct).ConfigureAwait(false);
            else
            {
                await WaitForMesh(ct).ConfigureAwait(false);
                // The centre is often out on the water: walk to the nearest ground, and stop as soon as the line can be cast.
                var target = await Game.Run(() =>
                    Ipc<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable", place.Centre with { Y = PlayerHeight() }, place.Radius, 60f)
                    ?? place.Centre).ConfigureAwait(false);
                if (!await Game.Run(() => MoveCloseTo(target, 2f)).ConfigureAwait(false))
                    throw new ToolException($"vnavmesh could not find a path to {place.Name}.");
                steps.Add($"vnavmesh: walking to {place.Name}.");
                phase = $"walking to {place.Name}";
                await Task.Delay(500, ct).ConfigureAwait(false);
                while (await Game.Run(() => PathRunning).ConfigureAwait(false))
                {
                    if (await Game.Run(CanCast).ConfigureAwait(false)) break;
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                await Game.Run(() => { StopMoving(); return true; }).ConfigureAwait(false);
            }
        }

        return await Game.Run(() => new
        {
            spot = new { id = place.Spot, name = place.Name },
            zone = TerritoryName(place.Territory),
            canCast = CanCast(),
            hint = CanCast() ? null : Svc.Objects.LocalPlayer?.ClassJob.RowId == FisherJob
                ? "Cast cannot be used here yet: step closer to the water and face it."
                : "Switch to Fisher (switch_gearset) to fish here.",
        }).ConfigureAwait(false);
    }

    private static float PlayerHeight() => Svc.Objects.LocalPlayer?.Position.Y ?? 0;
}
