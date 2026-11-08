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
        bool Here() => AtSpot(place);

        if (!await Game.Run(Here).ConfigureAwait(false))
        {
            await TravelNear(new NpcSpot(0, place.Name, place.Territory, place.Map, place.Centre, false), steps, ct).ConfigureAwait(false);
            if (!VnavmeshLoaded)
                await GuideWalk(place.Territory, place.Centre, place.Name, place.Radius, Here, steps, ct).ConfigureAwait(false);
            else
            {
                await WaitForMesh(ct).ConfigureAwait(false);
                // The centre is often out on the water: walk to the nearest ground, and stop as soon as the line can be cast.
                // Cloud-sea spots (the Churning Mists, the Sea of Clouds) have their centre over the void, far from the island edge
                // and at another height: widen the search before falling back to the centre, where nobody can land.
                var target = await Game.Run(() =>
                {
                    foreach (var (radius, height) in new[] { (place.Radius, 60f), (place.Radius * 2, 300f), (place.Radius * 4, 600f) })
                        if (Ipc<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable", place.Centre with { Y = PlayerHeight() }, radius, height) is { } ground)
                            return ground;
                    return place.Centre;
                }).ConfigureAwait(false);
                if (!await StartPath(target, 2f, steps, ct).ConfigureAwait(false))
                    throw new ToolException($"vnavmesh could not find a path to {place.Name}.");
                steps.Add($"vnavmesh: walking to {place.Name}.");
                phase = $"walking to {place.Name}";
                await Task.Delay(500, ct).ConfigureAwait(false);
                while (await Game.Run(() => PathRunning).ConfigureAwait(false))
                {
                    if (await Game.Run(() => AtSpot(place)).ConfigureAwait(false)) break;
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                await Game.Run(() => { StopMoving(); return true; }).ConfigureAwait(false);
                await GetOffMount(steps, ct).ConfigureAwait(false);
            }
        }

        // Near the spot but not at the water: find the shore and walk to it, facing the water.
        if (VnavmeshLoaded && !await Game.Run(Here).ConfigureAwait(false) && await Game.Run(() => Svc.Objects.LocalPlayer?.ClassJob.RowId == FisherJob).ConfigureAwait(false))
            await WalkToShore(place, steps, ct).ConfigureAwait(false);

        return await Game.Run(() => new
        {
            spot = new { id = place.Spot, name = place.Name },
            zone = TerritoryName(place.Territory),
            canCast = AtSpot(place),
            hint = AtSpot(place) ? null : Svc.Objects.LocalPlayer?.ClassJob.RowId == FisherJob
                ? "Cast cannot be used here yet: step closer to the water and face it."
                : "Switch to Fisher (switch_gearset) to fish here.",
        }).ConfigureAwait(false);
    }

    /// <summary>Near this spot (casting elsewhere fishes another spot), and as a Fisher, able to cast. Framework thread.</summary>
    internal static bool AtSpot(FishingPlace place) =>
        Svc.ClientState.TerritoryType == place.Territory && Svc.Objects.LocalPlayer is { } p &&
        Vector2.Distance(new(p.Position.X, p.Position.Z), new(place.Centre.X, place.Centre.Z)) <= (p.ClassJob.RowId == FisherJob ? ReachAsFisher(place) : place.Radius) &&
        (p.ClassJob.RowId != FisherJob || CanCast());

    /// <summary>How far from a spot's centre a Fisher may cast and still fish it: the shore search radius, or farther where the
    /// nearest edge lies farther out (cloud-sea spots, whose centre is over the void).</summary>
    private static readonly Dictionary<uint, float> EdgeReach = new();

    private static float ReachAsFisher(FishingPlace place) =>
        EdgeReach.TryGetValue(place.Spot, out var edge) ? Math.Max(ShoreSearch, edge + 5) : ShoreSearch;

    /// <summary>
    /// Walks to the walkable point nearest the spot's centre, which is the edge of the land (a cloud-sea island, a pier) when the
    /// centre lies off the mesh, arriving from a few yalms inland so the character faces the spot. Tries the points around the
    /// ring at that distance as well. Returns whether Cast can be used. Framework calls inside.
    /// </summary>
    private static async Task<bool> WalkToEdge(FishingPlace place, List<string> steps, CancellationToken ct)
    {
        var edges = await Game.Run(() =>
        {
            var height = PlayerHeight();
            var list = new List<Vector3>();
            if (Ipc<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable", place.Centre with { Y = height }, ShoreSearch * 4, 300f) is not { } nearest)
                return list;
            list.Add(nearest);
            var ring = Vector2.Distance(new(nearest.X, nearest.Z), new(place.Centre.X, place.Centre.Z));
            foreach (var (x, z) in XivMcp.Fishing.ShoreFinder.Sweep(place.Centre.X, place.Centre.Z, ring, 12))
                if (Ipc<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable", new Vector3((float)x, nearest.Y, (float)z), 8f, 30f) is { } point)
                    list.Add(point);
            return list;
        }).ConfigureAwait(false);
        if (edges.Count == 0) return false;
        var reach = Vector2.Distance(new(edges[0].X, edges[0].Z), new(place.Centre.X, place.Centre.Z));
        EdgeReach[place.Spot] = reach;
        foreach (var edge in edges.Take(8))
        {
            ct.ThrowIfCancellationRequested();
            phase = $"looking for the edge at {place.Name}";
            var (ix, iz) = XivMcp.Fishing.ShoreFinder.ApproachFrom(edge.X, edge.Z, place.Centre.X, place.Centre.Z);
            foreach (var target in new[] { new Vector3((float)ix, edge.Y, (float)iz), edge })
            {
                if (!await Game.Run(() => MoveCloseTo(target, 0.5f)).ConfigureAwait(false)) break;
                await Task.Delay(300, ct).ConfigureAwait(false);
                var until = DateTime.UtcNow.AddSeconds(20);
                while (await Game.Run(() => PathRunning).ConfigureAwait(false) && DateTime.UtcNow < until)
                    await Task.Delay(150, ct).ConfigureAwait(false);
                await Game.Run(() => { StopMoving(); return true; }).ConfigureAwait(false);
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
            if (await Game.Run(() => AtSpot(place)).ConfigureAwait(false))
            {
                steps.Add($"vnavmesh: walked to the edge at {place.Name}, facing the spot.");
                return true;
            }
        }
        return false;
    }

    private static float PlayerHeight() => Svc.Objects.LocalPlayer?.Position.Y ?? 0;

    /// <summary>How far from a spot's centre to look for its shore, in yalms.</summary>
    private const float ShoreSearch = 40;

    /// <summary>
    /// Tries the shore points around the spot, nearest first: walks to a point a few yalms inland, then out to the edge, so the
    /// character faces the water, until Cast can be used.
    /// </summary>
    private static async Task WalkToShore(FishingPlace place, List<string> steps, CancellationToken ct)
    {
        if (await WalkToEdge(place, steps, ct).ConfigureAwait(false)) return;
        var candidates = await Game.Run(() =>
        {
            var p = Svc.Objects.LocalPlayer!.Position;
            bool Walkable(double x, double z) =>
                Ipc<Vector3, float, bool, bool>("vnavmesh.Query.Mesh.IsPointOnMesh", new Vector3((float)x, p.Y, (float)z), 15f, false);
            return XivMcp.Fishing.ShoreFinder.Candidates(place.Centre.X, place.Centre.Z, ShoreSearch, Walkable, p.X, p.Z);
        }).ConfigureAwait(false);
        foreach (var shore in candidates.Take(6))
        {
            ct.ThrowIfCancellationRequested();
            phase = $"looking for the water at {place.Name}";
            var height = await Game.Run(PlayerHeight).ConfigureAwait(false);
            foreach (var (x, z) in new[] { (shore.InlandX, shore.InlandZ), (shore.X, shore.Z) })
            {
                if (!await Game.Run(() => MoveCloseTo(new Vector3((float)x, height, (float)z), 0.5f)).ConfigureAwait(false)) break;
                await Task.Delay(300, ct).ConfigureAwait(false);
                var until = DateTime.UtcNow.AddSeconds(15);
                while (await Game.Run(() => PathRunning).ConfigureAwait(false) && DateTime.UtcNow < until)
                {
                    if (await Game.Run(() => AtSpot(place)).ConfigureAwait(false)) break;
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
                await Game.Run(() => { StopMoving(); return true; }).ConfigureAwait(false);
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
            if (await Game.Run(() => AtSpot(place)).ConfigureAwait(false))
            {
                steps.Add($"vnavmesh: walked to the water's edge at {place.Name}.");
                return;
            }
        }
        // No edge found (a shallow river bed is walkable, so the ground never ends): walk out from the centre in every direction
        // and stop where Cast becomes usable, facing the water.
        foreach (var (x, z) in XivMcp.Fishing.ShoreFinder.Sweep(place.Centre.X, place.Centre.Z, ShoreSearch))
        {
            ct.ThrowIfCancellationRequested();
            phase = $"looking for the water at {place.Name}";
            var height = await Game.Run(PlayerHeight).ConfigureAwait(false);
            var target = await Game.Run(() =>
                Ipc<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable", new Vector3((float)x, height, (float)z), 8f, 60f)
                ?? new Vector3((float)x, height, (float)z)).ConfigureAwait(false);
            if (!await Game.Run(() => MoveCloseTo(target, 0.5f)).ConfigureAwait(false)) continue;
            await Task.Delay(300, ct).ConfigureAwait(false);
            var until = DateTime.UtcNow.AddSeconds(25);
            var cast = false;
            while (await Game.Run(() => PathRunning).ConfigureAwait(false) && DateTime.UtcNow < until)
            {
                if (cast = await Game.Run(() => AtSpot(place)).ConfigureAwait(false)) break;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            await Game.Run(() => { StopMoving(); return true; }).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
            if (cast || await Game.Run(() => AtSpot(place)).ConfigureAwait(false))
            {
                steps.Add($"vnavmesh: walked out to the water at {place.Name}.");
                return;
            }
        }
        steps.Add($"Could not find a place to cast at {place.Name}; step to the water and face it.");
    }
}
