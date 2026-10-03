using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;
using static XivMcp.Util.Navigation;

namespace XivMcp.Tools;

/// <summary>Travelling to an NPC anywhere in the world: teleport (aetheryte or aethernet shard closest to the NPC), then walk.</summary>
internal static partial class NavigationTools
{

    /// <summary>A place where an NPC stands.</summary>
    internal sealed record NpcSpot(uint NpcId, string Name, uint Territory, uint Map, Vector3 Position, bool ExactHeight);

    /// <summary>An NPC spot from a zone and map coordinates (as Item Vendor Location reports them, on the zone's default map). Framework thread.</summary>
    internal static NpcSpot? FindNpc(uint npcId, uint territory, float mapX, float mapY)
    {
        if (territory == 0 || Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.Map.ValueNullable is not { } map) return null;
        return new NpcSpot(npcId, ItemSourceTools.NpcName(npcId), territory, map.RowId, MapToWorld(map, mapX, mapY), false);
    }

    /// <summary>Map coordinates (as shown on the in-game map) → world X/Z. Y is unknown (0).</summary>
    internal static Vector3 MapToWorld(Map map, double x, double y)
    {
        var scale = map.SizeFactor / 100.0;
        double World(double c, short offset) => ((c - 1) * scale * 2048 / 41 - 1024) / scale - offset;
        return new Vector3((float)World(x, map.OffsetX), 0, (float)World(y, map.OffsetY));
    }

    /// <summary>World X/Z → map coordinates.</summary>
    internal static (double X, double Y) WorldToMap(Map map, Vector3 p)
    {
        var scale = map.SizeFactor / 100.0;
        double Coord(float w, short offset) => Math.Round(41 / scale * (((w + offset) * scale + 1024) / 2048) + 1, 1);
        return (Coord(p.X, map.OffsetX), Coord(p.Z, map.OffsetY));
    }

    /// <summary>Travels to an NPC and walks up to it. Returns where it ended up.</summary>
    internal static async Task<object> GoToNpc(NpcSpot spot, List<string> steps, CancellationToken ct)
    {
        await Game.RunLoggedIn(() => { EnsureCanTravel(); return true; }).ConfigureAwait(false);

        // Already close enough to see it?
        if (await WalkTo(spot.Name, o => o.BaseId == spot.NpcId, steps, ct).ConfigureAwait(false) is { } near) return near;

        var plan = await Game.Run(() => PlanTeleport(spot)).ConfigureAwait(false);
        if (plan is not null)
        {
            if (!LifestreamLoaded) throw new ToolException($"{spot.Name} is in {TerritoryName(spot.Territory)}; travelling there needs Lifestream, which is not installed.");
            phase = $"travelling to {plan.Value.Label}";
            var ok = await Game.Run(() =>
            {
                try
                {
                    // "/li <aethernet shard>" teleports to that city's aetheryte, walks to it and takes the aethernet.
                    if (plan.Value.Shard) { Svc.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(plan.Value.Label); return true; }
                    return Svc.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport").InvokeFunc(plan.Value.AetheryteId, 0);
                }
                catch (Exception ex) { Svc.Log.Warning($"[MCP] Lifestream travel failed: {ex.Message}"); return false; }
            }).ConfigureAwait(false);
            if (!ok) throw new ToolException($"Lifestream could not travel to {plan.Value.Label} (is the aetheryte attuned?).");
            steps.Add($"Lifestream: {(plan.Value.Shard ? "aethernet" : "teleport")} to {plan.Value.Label}.");
            await WaitUntilSettled(ct).ConfigureAwait(false);
            if (await Game.Run(() => Svc.ClientState.TerritoryType).ConfigureAwait(false) != spot.Territory)
                throw new ToolException($"Ended up in {await Game.Run(() => TerritoryName(Svc.ClientState.TerritoryType)).ConfigureAwait(false)} instead of {TerritoryName(spot.Territory)}.");
        }

        if (await WalkTo(spot.Name, o => o.BaseId == spot.NpcId, steps, ct).ConfigureAwait(false) is { } arrived) return arrived;

        // Not in sight yet: walk towards where it stands, then look again.
        if (!VnavmeshLoaded) throw new ToolException($"{spot.Name} is not nearby; walking there needs vnavmesh, which is not installed.");
        await WaitForMesh(ct).ConfigureAwait(false);
        var target = await Game.Run(() => spot.ExactHeight ? spot.Position
            : Ipc<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor", spot.Position with { Y = 1024 }, false, 5f) ?? spot.Position).ConfigureAwait(false);
        if (!await Game.Run(() => MoveCloseTo(target, 3f)).ConfigureAwait(false))
            throw new ToolException($"vnavmesh could not find a path to {spot.Name}.");
        steps.Add($"vnavmesh: walking towards {spot.Name}.");
        phase = $"walking to {spot.Name}";
        await Task.Delay(500, ct).ConfigureAwait(false);
        while (await Game.Run(() => PathRunning).ConfigureAwait(false))
        {
            // Stop as soon as the NPC is close; the final approach uses its real position.
            if (await Game.Run(() => Svc.Objects.Any(o => o.BaseId == spot.NpcId && Game.DistanceToPlayer(o.Position) < 20)).ConfigureAwait(false)) break;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        await Game.Run(() => { StopMoving(); return true; }).ConfigureAwait(false);
        return await WalkTo(spot.Name, o => o.BaseId == spot.NpcId, steps, ct).ConfigureAwait(false)
               ?? throw new ToolException($"Arrived where {spot.Name} should be, but the NPC isn't there (it may only appear during a quest or event).");
    }

    private static async Task WaitForMesh(CancellationToken ct)
    {
        var waited = DateTime.UtcNow;
        while (!await Game.Run(() => NavReady).ConfigureAwait(false))
        {
            if (DateTime.UtcNow - waited > TimeSpan.FromSeconds(90)) throw new ToolException("vnavmesh did not finish building the navmesh for this zone.");
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The aetheryte or aethernet shard to use, or null to just walk: the one nearest to the NPC in its zone, unless the player is
    /// already in that zone and closer than it.
    /// </summary>
    private static (uint AetheryteId, string Label, bool Shard)? PlanTeleport(NpcSpot spot)
    {
        var options = Svc.Data.GetExcelSheet<Aetheryte>()
            .Where(a => a.Territory.RowId == spot.Territory && (a.IsAetheryte || a.AethernetName.RowId != 0))
            .Select(a => (Row: a, Pos: AetherytePosition(a)))
            .ToList();
        var inZone = Svc.ClientState.TerritoryType == spot.Territory;
        var playerDistance = inZone && Svc.Objects.LocalPlayer is { } p ? Flat(p.Position, spot.Position) : float.MaxValue;
        if (options.Count == 0)
            return inZone ? null : throw new ToolException($"{TerritoryName(spot.Territory)} has no aetheryte to travel to; go there first, then retry.");

        var best = options.OrderBy(o => o.Pos is { } pos ? Flat(pos, spot.Position) : float.MaxValue).ThenByDescending(o => o.Row.IsAetheryte).First();
        var bestDistance = best.Pos is { } bp ? Flat(bp, spot.Position) : float.MaxValue;
        if (inZone && playerDistance <= bestDistance + 30) return null;

        if (best.Row.IsAetheryte) return (best.Row.RowId, Excel.Name(best.Row.PlaceName) ?? $"aetheryte {best.Row.RowId}", false);
        return (best.Row.RowId, Excel.Name(best.Row.AethernetName) ?? $"aethernet {best.Row.RowId}", true);
    }

    private static Vector3? AetherytePosition(Aetheryte a)
    {
        foreach (var level in a.Level)
            if (level.RowId != 0 && level.ValueNullable is { } l) return new Vector3(l.X, l.Y, l.Z);
        // Main aetherytes often have no Level row; their map marker gives the position.
        var found = Svc.Data.GetSubrowExcelSheet<MapMarker>().SelectMany(m => m)
            .Where(m => m.DataType == 3 && m.DataKey.RowId == a.RowId).Select(m => (MapMarker?)m).FirstOrDefault();
        if (found is { } marker && a.Map.ValueNullable is { } map)
        {
            // MapMarker X/Y are map pixels (0-2048).
            var scale = map.SizeFactor / 100f;
            return new Vector3((marker.X - 1024) / scale - map.OffsetX, 0, (marker.Y - 1024) / scale - map.OffsetY);
        }
        return null;
    }

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    internal static string TerritoryName(uint territory) =>
        Excel.Name(Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceName ?? default) ?? $"territory {territory}";

    private static TResult? Ipc<T1, T2, T3, TResult>(string name, T1 a, T2 b, T3 c)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T1, T2, T3, TResult>(name).InvokeFunc(a, b, c); }
        catch (Exception ex) { Svc.Log.Debug($"IPC {name} failed: {ex.Message}"); return default; }
    }
}
