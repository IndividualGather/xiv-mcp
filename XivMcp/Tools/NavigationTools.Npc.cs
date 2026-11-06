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

/// <summary>
/// Travelling to an NPC anywhere in the world: teleport (aetheryte or aethernet shard closest to the NPC), then walk. Without
/// Lifestream or vnavmesh, the player is asked to teleport or to walk to the map flag, and XIV MCP waits.
/// </summary>
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
        // Map positions have no height: the nearest shard (or the spot the player stands at) can be on a level the NPC can't be
        // walked to from. Then travel once more, by another aetheryte or shard.
        var tried = new HashSet<uint>();
        try
        {
            return await GoToNpcOnce(spot, steps, retry: false, tried, ct).ConfigureAwait(false);
        }
        catch (ToolException ex) when (ex.Message.Contains("runs into an obstacle", StringComparison.Ordinal) ||
                                       ex.Message.StartsWith("vnavmesh could not find a path", StringComparison.Ordinal))
        {
            steps.Add($"{ex.Message} Travelling there by another aetheryte or shard.");
            return await GoToNpcOnce(spot, steps, retry: true, tried, ct).ConfigureAwait(false);
        }
    }

    private static async Task<object> GoToNpcOnce(NpcSpot spot, List<string> steps, bool retry, HashSet<uint> tried, CancellationToken ct)
    {
        // Already close enough to see it?
        if (!retry && await WalkTo(spot.Name, o => o.BaseId == spot.NpcId, steps, ct).ConfigureAwait(false) is { } near) return near;

        await TravelNear(spot, steps, ct, retry, tried).ConfigureAwait(false);

        if (await WalkTo(spot.Name, o => o.BaseId == spot.NpcId, steps, ct).ConfigureAwait(false) is { } arrived) return arrived;

        // Not in sight yet: walk towards where it stands, then look again.
        if (!VnavmeshLoaded)
        {
            await GuideWalk(spot.Territory, spot.Position, spot.Name, ArriveRange + 2,
                () => Svc.Objects.Any(o => o.BaseId == spot.NpcId && Game.DistanceToPlayer(o.Position) <= ArriveRange + 1), steps, ct).ConfigureAwait(false);
            return await WalkTo(spot.Name, o => o.BaseId == spot.NpcId, steps, ct).ConfigureAwait(false)
                   ?? throw new ToolException($"You are where {spot.Name} should be, but the NPC is not there (it may only appear during a quest or event).");
        }
        // Map coordinates carry no height: walk to the nearest reachable point at the player's height (the highest floor there can be
        // a roof in a city with several levels), and stop as soon as the NPC is close; the final approach uses its real position.
        if (spot.ExactHeight)
            await WalkTowardsExact(spot.Position, spot.Name, () => Svc.Objects.Any(o => o.BaseId == spot.NpcId && Game.DistanceToPlayer(o.Position) < 20), steps, ct).ConfigureAwait(false);
        else
            await WalkTowards(spot.Position, spot.Name, () => Svc.Objects.Any(o => o.BaseId == spot.NpcId && Game.DistanceToPlayer(o.Position) < 20), steps, ct).ConfigureAwait(false);
        return await WalkTo(spot.Name, o => o.BaseId == spot.NpcId, steps, ct).ConfigureAwait(false)
               ?? throw new ToolException($"Arrived where {spot.Name} should be, but the NPC isn't there (it may only appear during a quest or event).");
    }

    /// <summary>
    /// Gets into the zone of <paramref name="spot"/>, near it: with Lifestream by teleport or aethernet, otherwise by asking the player
    /// to teleport and waiting. Does nothing when walking from where the player is would be as quick.
    /// </summary>
    internal static async Task TravelNear(NpcSpot spot, List<string> steps, CancellationToken ct, bool retry = false, ICollection<uint>? tried = null)
    {
        var plan = await Game.Run(() => PlanTeleport(spot, retry, tried)).ConfigureAwait(false);
        if (plan is null && retry) throw new ToolException($"No other aetheryte or aethernet shard leads to {spot.Name}.");
        if (plan is { } chosen) tried?.Add(chosen.AetheryteId);
        if (plan is null) steps.Add(await Game.Run(() => WalkReason(spot)).ConfigureAwait(false));
        if (plan is not null && !LifestreamLoaded)
            await GuideTeleport(plan.Value.AetheryteId, plan.Value.Shard, spot.Territory, steps, ct).ConfigureAwait(false);
        else if (plan is not null)
        {
            phase = $"travelling to {plan.Value.Label}";
            // Lifestream can't leave an inn room; the game's own teleport can.
            var exit = await Game.Run(() =>
            {
                var sheet = Svc.Data.GetExcelSheet<Aetheryte>();
                var target = sheet.GetRow(plan.Value.AetheryteId);
                uint? city = plan.Value.Shard && target.AethernetGroup != 0 &&
                             sheet.FirstOrDefault(a => a.IsAetheryte && a.AethernetGroup == target.AethernetGroup) is { RowId: not 0 } c ? c.RowId : null;
                return (Plan: XivMcp.Maps.InnExit.Plan(CurrentIntendedUse(), plan.Value.AetheryteId, plan.Value.Shard, city),
                        CityName: city is { } id ? Excel.Name(sheet.GetRow(id).PlaceName) : null);
            }).ConfigureAwait(false);
            if (exit.Plan.Teleport is { } first)
            {
                var room = await Game.Run(() => Svc.ClientState.TerritoryType).ConfigureAwait(false);
                var teleported = await Game.Run(() => GameTeleport(first)).ConfigureAwait(false);
                if (!teleported) throw new ToolException($"The teleport out of the inn room to {exit.CityName ?? plan.Value.Label} was refused (is the aetheryte attuned, is there enough gil?).");
                steps.Add($"Teleported out of the inn room to {exit.CityName ?? plan.Value.Label}.");
                var left = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (await Game.Run(() => Svc.ClientState.TerritoryType).ConfigureAwait(false) == room)
                {
                    if (DateTime.UtcNow > left) throw new ToolException("The teleport out of the inn room did not happen (interrupted?).");
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                await WaitUntilSettled(ct).ConfigureAwait(false);
                if (!exit.Plan.ThenAethernet)
                {
                    if (await Game.Run(() => Svc.ClientState.TerritoryType).ConfigureAwait(false) != spot.Territory)
                        throw new ToolException($"Ended up in {await Game.Run(() => TerritoryName(Svc.ClientState.TerritoryType)).ConfigureAwait(false)} instead of {TerritoryName(spot.Territory)}.");
                    return;
                }
            }
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
    }

    /// <summary>Teleports with the game's own teleport, as the Teleport window does. Framework thread.</summary>
    private static unsafe bool GameTeleport(uint aetheryteId) =>
        FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance()->Teleport(aetheryteId, 0);

    private static async Task WaitForMesh(CancellationToken ct)
    {
        var waited = DateTime.UtcNow;
        while (!await Game.Run(() => NavReady).ConfigureAwait(false))
        {
            if (DateTime.UtcNow - waited > TimeSpan.FromSeconds(90)) throw new ToolException("vnavmesh did not finish building the navmesh for this zone.");
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Why travel walks instead of teleporting: how far the player is, and the nearest aetheryte or shard with a known position.</summary>
    private static string WalkReason(NpcSpot spot)
    {
        var known = Svc.Data.GetExcelSheet<Aetheryte>()
            .Where(a => a.Territory.RowId == spot.Territory && (a.IsAetheryte || a.AethernetName.RowId != 0))
            .Select(a => (Name: a.IsAetheryte ? Excel.Name(a.PlaceName) : Excel.Name(a.AethernetName), Pos: AetherytePosition(a)))
            .ToList();
        var located = known.Where(k => k.Pos is not null).Select(k => (k.Name, Distance: Flat(k.Pos!.Value, spot.Position))).OrderBy(k => k.Distance).ToList();
        var player = Svc.Objects.LocalPlayer is { } p ? Flat(p.Position, spot.Position) : float.NaN;
        return $"Walking: {player:0} yalms to {spot.Name}; " + (located.Count == 0
            ? $"none of the zone's {known.Count} aetherytes/shards has a known position."
            : $"nearest is {located[0].Name} ({located[0].Distance:0} yalms from it; {located.Count} of {known.Count} located).");
    }

    /// <summary>
    /// The aetheryte or aethernet shard to use, or null to just walk: the one nearest to the NPC in its zone, unless the player is
    /// already in that zone and closer than it.
    /// </summary>
    private static (uint AetheryteId, string Label, bool Shard)? PlanTeleport(NpcSpot spot, bool retry = false, ICollection<uint>? tried = null)
    {
        var rows = Svc.Data.GetExcelSheet<Aetheryte>()
            .Where(a => a.Territory.RowId == spot.Territory && (a.IsAetheryte || a.AethernetName.RowId != 0))
            .ToDictionary(a => a.RowId);
        var inZone = Svc.ClientState.TerritoryType == spot.Territory;
        var player = inZone ? Svc.Objects.LocalPlayer?.Position : null;
        if (rows.Count == 0)
            return inZone ? null : throw new ToolException($"{TerritoryName(spot.Territory)} has no aetheryte to travel to; go there first, then retry.");

        var options = rows.Values.Select(a => AetherytePosition(a) is { } pos
                ? new XivMcp.Maps.TeleportOption(a.RowId, a.IsAetheryte, Flat(pos, spot.Position), player is { } pp ? Flat(pos, pp) : null)
                : new XivMcp.Maps.TeleportOption(a.RowId, a.IsAetheryte, null, null))
            .ToList();
        var choice = XivMcp.Maps.TeleportPick.Choose(options, player is { } pl ? Flat(pl, spot.Position) : null, retry, tried ?? []);
        if (choice is not { } c) return null;
        var best = rows[c.Id];
        if (best.IsAetheryte) return (best.RowId, Excel.Name(best.PlaceName) ?? $"aetheryte {best.RowId}", false);
        return (best.RowId, Excel.Name(best.AethernetName) ?? $"aethernet {best.RowId}", true);
    }

    internal static Vector3? AetherytePosition(Aetheryte a)
    {
        foreach (var level in a.Level)
            if (level.RowId != 0 && level.ValueNullable is { } l) return new Vector3(l.X, l.Y, l.Z);
        // Aetherytes and aethernet shards often have no Level row; their map marker gives the position. Shards have no map of
        // their own: their marker is on the zone's map. Aetheryte markers point at the aetheryte (data type 3); shard markers are
        // plain markers named after the shard (in Solution Nine: data type 0, the aethernet icon, the shard's place name).
        // A shard's Map is row 0, which exists (an empty default row): only take a map that is really set.
        if ((a.Map.RowId != 0 ? a.Map.ValueNullable : a.Territory.ValueNullable?.Map.ValueNullable) is not { } map) return null;
        bool Points(MapMarker m) => m.DataType is 3 or 4 && m.DataKey.RowId == a.RowId;
        // The marker's place name is a different PlaceName row with the same text (e.g. "Nexus Arcade" 4647 vs the shard's 4738).
        var shardName = a.IsAetheryte ? null : Excel.Name(a.AethernetName);
        bool Named(MapMarker m) => !string.IsNullOrEmpty(shardName) && m.PlaceNameSubtext.RowId != 0
                                   && string.Equals(Excel.Name(m.PlaceNameSubtext), shardName, StringComparison.OrdinalIgnoreCase);
        var onMap = Svc.Data.GetSubrowExcelSheet<MapMarker>().GetRowOrDefault(map.MapMarkerRange);
        var found = onMap?.Where(Points).Select(m => (MapMarker?)m).FirstOrDefault()
                    ?? onMap?.Where(Named).Select(m => (MapMarker?)m).FirstOrDefault()
                    ?? Svc.Data.GetSubrowExcelSheet<MapMarker>().SelectMany(m => m).Where(Points).Select(m => (MapMarker?)m).FirstOrDefault();
        if (found is { } marker)
        {
            // MapMarker X/Y are map pixels (0-2048).
            var scale = map.SizeFactor / 100f;
            return new Vector3((marker.X - 1024) / scale - map.OffsetX, 0, (marker.Y - 1024) / scale - map.OffsetY);
        }
        return null;
    }

    /// <summary>Where a map shows markers with this icon (world X/Z; Y unknown, 0). Framework thread.</summary>
    internal static List<Vector3> MarkerPositions(uint mapId, uint icon)
    {
        if (Svc.Data.GetExcelSheet<Map>().GetRowOrDefault(mapId) is not { } map) return [];
        if (Svc.Data.GetSubrowExcelSheet<MapMarker>().GetRowOrDefault(map.MapMarkerRange) is not { } markers) return [];
        var scale = map.SizeFactor / 100f;
        return markers.Where(m => m.Icon == icon)
            .Select(m => new Vector3((m.X - 1024) / scale - map.OffsetX, 0, (m.Y - 1024) / scale - map.OffsetY)).ToList();
    }

    /// <summary>
    /// Walks towards a spot in the current zone that is too far away to be loaded as an object yet (its height unknown), until
    /// the path ends or <paramref name="seen"/> is true; without vnavmesh the player is asked to walk there.
    /// </summary>
    internal static async Task WalkTowards(Vector3 position, string label, Func<bool> seen, List<string> steps, CancellationToken ct)
    {
        var territory = await Game.Run(() => Svc.ClientState.TerritoryType).ConfigureAwait(false);
        if (!VnavmeshLoaded)
        {
            await GuideWalk(territory, position, label, 10f, seen, steps, ct).ConfigureAwait(false);
            return;
        }
        await WaitForMesh(ct).ConfigureAwait(false);
        var target = await Game.Run(() =>
            Ipc<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable", position with { Y = PlayerHeight() }, 10f, 60f)
            ?? position).ConfigureAwait(false);
        await WalkTowardsExact(target, label, seen, steps, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Walks to a point whose height is known until the path ends or <paramref name="seen"/> is true; stops when the walk no longer
    /// gets closer (running into a wall keeps the path "running").
    /// </summary>
    private static async Task WalkTowardsExact(Vector3 target, string label, Func<bool> seen, List<string> steps, CancellationToken ct)
    {
        await WaitForMesh(ct).ConfigureAwait(false);
        if (!await StartPath(target, 3f, steps, ct).ConfigureAwait(false))
            throw new ToolException($"vnavmesh could not find a path to {label}.");
        steps.Add($"vnavmesh: walking towards {label}.");
        phase = $"walking to {label}";
        await Task.Delay(500, ct).ConfigureAwait(false);
        var approach = new Maps.Approach(TimeSpan.FromSeconds(4));
        while (await Game.Run(() => PathRunning && !seen()).ConfigureAwait(false))
        {
            // Computing a long path takes a while before the character moves: only judge progress while it walks.
            if (!await Game.Run(() => PathWalking).ConfigureAwait(false))
            {
                approach = new Maps.Approach(TimeSpan.FromSeconds(4));
                await Task.Delay(250, ct).ConfigureAwait(false);
                continue;
            }
            var distance = await Game.Run(() => Svc.Objects.LocalPlayer is { } p ? Flat(p.Position, target) : float.MaxValue).ConfigureAwait(false);
            if (approach.Stalled(distance, DateTime.UtcNow))
            {
                await Game.Run(() => { StopMoving(); return true; }).ConfigureAwait(false);
                throw new ToolException($"Stuck on the way to {label} ({distance:0} yalms away): the path runs into an obstacle.");
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        await Game.Run(() => { StopMoving(); return true; }).ConfigureAwait(false);
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
