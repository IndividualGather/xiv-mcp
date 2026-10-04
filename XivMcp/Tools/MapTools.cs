using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Map = Lumina.Excel.Sheets.Map;
using XivMcp.Maps;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Where the player is (zone, area, map coordinates), the map flag (set, clear, read), waiting until the player reaches a spot, and
/// routes: a job that flags each stop once the one before is reached. The conversions and the route plan live in XivMcp.Core.Maps.
/// </summary>
internal static class MapTools
{
    private const string ZoneProperty = """
        "zone": { "type": "string", "description": "Zone name (e.g. \"Central Shroud\", \"Limsa Lominsa Lower Decks\") or territory id. Default: the zone you are in." }
        """;

    private const string CoordProperties = """
        "x": { "type": "number", "description": "Map X coordinate, as the in-game map shows it (about 1 to 42)." },
        "y": { "type": "number", "description": "Map Y coordinate, as the in-game map shows it." }
        """;

    public static IEnumerable<McpTool> Create(Func<JobManager> jobs, Func<string?> client)
    {
        yield return new McpTool
        {
            Name = "get_position",
            Description = "Where the player is: zone (territory), region, area and sub-area names, the current map, map coordinates as the in-game " +
                          "map shows them (X/Y), the world position (yalms), and the map flag if one is set (its zone, coordinates and distance).",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
            {
                var cs = Svc.ClientState;
                var player = Svc.Objects.LocalPlayer ?? throw new ToolException("The character isn't in the world right now (loading screen or cutscene).");
                var territory = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(cs.TerritoryType);
                var map = Svc.Data.GetExcelSheet<Map>().GetRowOrDefault(cs.MapId) ?? territory?.Map.ValueNullable;
                unsafe
                {
                    var info = TerritoryInfo.Instance();
                    return new
                    {
                        zone = territory is { } t ? new { id = t.RowId, name = Excel.Name(t.PlaceName) } : null,
                        region = territory is { } r ? Excel.Name(r.PlaceNameRegion) : null,
                        area = info is null || info->AreaPlaceNameId == 0 ? null : Excel.NameOf<PlaceName>(info->AreaPlaceNameId),
                        subArea = info is null || info->SubAreaPlaceNameId == 0 ? null : Excel.NameOf<PlaceName>(info->SubAreaPlaceNameId),
                        map = map is { } m ? new { id = m.RowId, name = MapName(m), coordinates = Coords(m, player.Position.X, player.Position.Z) } : null,
                        world = Game.Pos(player.Position),
                        flag = Flag(cs.TerritoryType, player.Position),
                    };
                }
            }),
        };

        yield return new McpTool
        {
            Name = "set_map_flag",
            Description = "Places the flag on the player's map (the same flag as right-clicking the map) at map coordinates in any zone, and opens " +
                          "the map at it by default. Only marks the spot: nothing moves. Use wait_until_arrived to wait for the player to get there, " +
                          "or start_route for several stops.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    {{ZoneProperty}},
                    {{CoordProperties}},
                    "open_map": { "type": "boolean", "description": "Open the map at the flag. Default true." },
                    "name": { "type": "string", "description": "What the spot is (shown as the map window title)." }
                  },
                  "required": ["x", "y"]
                }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                var (x, y) = MapCoordinates(args);
                var zone = args.String("zone");
                var open = args.Bool("open_map", true);
                var name = args.String("name");
                return Game.RunLoggedIn<object?>(() =>
                {
                    var (territory, map) = ResolveZone(zone);
                    var (wx, wz) = MapMath.ToWorld(Info(map), x, y);
                    unsafe
                    {
                        var agent = AgentMap.Instance();
                        agent->SetFlagMapMarker(territory.RowId, map.RowId, new Vector3((float)wx, 0, (float)wz));
                        if (open) agent->OpenMap(map.RowId, territory.RowId, name ?? "");
                    }
                    return new
                    {
                        flag = new { zone = Excel.Name(territory.PlaceName), territoryId = territory.RowId, map = MapName(map), x = Math.Round(x, 1), y = Math.Round(y, 1), name },
                        distance = Distance(territory.RowId, wx, wz),
                        mapOpened = open,
                    };
                });
            },
        };

        yield return new McpTool
        {
            Name = "clear_map_flag",
            Description = "Removes the flag from the player's map.",
            ReadOnly = false,
            Handler = (_, _) => Game.Run<object?>(() =>
            {
                unsafe
                {
                    var agent = AgentMap.Instance();
                    var had = agent->FlagMarkerCount > 0;
                    agent->FlagMarkerCount = 0;
                    return new { cleared = had };
                }
            }),
        };

        yield return new McpTool
        {
            Name = "wait_until_arrived",
            Description = "Waits until the player is in a zone and within a radius of map coordinates there (on the ground, ignoring height), then " +
                          "returns. Without coordinates it waits for the map flag. Does not move the character: the player (or another tool) does. " +
                          "Fails after the timeout, which leaves a job pending so it can be retried. Meant as a job step; start_route uses it.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    {{ZoneProperty}},
                    {{CoordProperties}},
                    "radius": { "type": "number", "description": "How close counts as arrived, in yalms. Default 10." },
                    "timeout_minutes": { "type": "integer", "description": "Give up after this long. Default 60, at most 1440." }
                  }
                }
                """,
            Handler = async (args, ct) =>
            {
                var radius = Math.Clamp(args.Float("radius") ?? 10f, 1f, 500f);
                var timeout = TimeSpan.FromMinutes(args.Int("timeout_minutes", 60, 1, 1440));
                var target = await Game.RunLoggedIn(() => Target(args)).ConfigureAwait(false);
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    // Null while there is no character in the world (loading screens, cutscenes): keep waiting.
                    var here = await Game.Run<(uint Territory, Vector3 Position)?>(() =>
                        Svc.Objects.LocalPlayer is { } p ? (Svc.ClientState.TerritoryType, p.Position) : null).ConfigureAwait(false);
                    if (here is { } h && MapMath.Arrived(h.Territory, h.Position.X, h.Position.Z, target.Territory, target.X, target.Z, radius))
                        return new { arrived = true, at = target.Describe, seconds = Math.Round(watch.Elapsed.TotalSeconds), world = Game.Pos(h.Position) };
                    if (watch.Elapsed > timeout)
                        throw new ToolException($"Didn't reach {target.Describe} within {timeout.TotalMinutes:0} minutes. The flag is still set; retry the step to keep waiting.");
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            },
        };

        yield return new McpTool
        {
            Name = "start_route",
            Description = "Starts a route as a background job: it flags the first stop on the player's map, waits until the player gets there, then " +
                          "flags the next, and clears the flag after the last. The player travels (walking, mounting, teleporting) however they like; " +
                          "XIV MCP only marks the way. Zones can change between stops. Follow it with get_job; change it with update_job like any job.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Route name shown in game (e.g. \"Hunt marks in Thanalan\")." },
                    "waypoints": {
                      "type": "array",
                      "description": "Stops in order, at most {{RoutePlan.MaxWaypoints}}. A stop without a zone is in the zone the player is in when the stop comes up.",
                      "items": {
                        "type": "object",
                        "properties": {
                          {{ZoneProperty}},
                          {{CoordProperties}},
                          "name": { "type": "string", "description": "What the stop is." },
                          "radius": { "type": "number", "description": "How close counts as arrived, in yalms (default: the route's radius)." }
                        },
                        "required": ["x", "y"]
                      }
                    },
                    "radius": { "type": "number", "description": "How close counts as arrived, in yalms. Default 10." },
                    "timeout_minutes": { "type": "integer", "description": "How long to wait for each stop before the job waits for you. Default 60." },
                    "open_map": { "type": "boolean", "description": "Open the map at each new flag. Default true." }
                  },
                  "required": ["waypoints"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, _) =>
            {
                IReadOnlyList<Waypoint> waypoints;
                IReadOnlyList<RouteStep> plan;
                try
                {
                    waypoints = RoutePlan.Parse(args.Node("waypoints") ?? throw new ArgumentException("'waypoints' is required."));
                    plan = RoutePlan.Steps(waypoints, Math.Clamp(args.Float("radius") ?? 10f, 1f, 500f), args.Int("timeout_minutes", 60, 1, 1440), args.Bool("open_map", true));
                }
                catch (ArgumentException ex) { throw new ToolException(ex.Message); }
                catch (InvalidOperationException ex) { throw new ToolException($"A waypoint has an invalid value: {ex.Message}"); }

                // Check every named zone now, so a typo fails here rather than halfway along the route.
                var zones = await Game.Run(() => waypoints.Select(w => w.Zone).Where(z => z is not null).Distinct()
                                                           .Select(z => Excel.Name(ResolveZone(z).Territory.PlaceName)).ToList()).ConfigureAwait(false);
                var name = args.String("name") ?? $"Route ({waypoints.Count} stops)";
                var steps = plan.Select(s => new JobManager.Step { Id = s.Id, Tool = s.Tool, Args = s.Args, Note = s.Note }).ToList();
                var job = jobs().Start(name, steps, client());
                return new { started = JobManager.Describe(job), stops = waypoints.Count, zones, poll = $"get_job id={job.Id}" };
            },
        };
    }

    // ---------------------------------------------------------------- helpers (framework thread)

    private static MapInfo Info(Map map) => new(map.SizeFactor, map.OffsetX, map.OffsetY);

    private static object Coords(Map map, float worldX, float worldZ)
    {
        var (x, y) = MapMath.ToMap(Info(map), worldX, worldZ);
        return new { x = Math.Round(x, 1), y = Math.Round(y, 1) };
    }

    /// <summary>The map's name: its place, and the sub-area for maps that show only part of a zone (e.g. a floor).</summary>
    private static string? MapName(Map map)
    {
        var name = Excel.Name(map.PlaceName);
        var sub = Excel.Name(map.PlaceNameSub);
        return string.IsNullOrEmpty(sub) || sub == name ? name : $"{name} - {sub}";
    }

    private static (double X, double Y) MapCoordinates(ToolArgs args)
    {
        var x = args.Float("x") ?? throw new ToolException("'x' (map X coordinate) is required.");
        var y = args.Float("y") ?? throw new ToolException("'y' (map Y coordinate) is required.");
        if (x is < 1 or > 42 || y is < 1 or > 42) throw new ToolException($"({x}, {y}) is off the map: map coordinates run from 1 to 42.");
        return (x, y);
    }

    /// <summary>
    /// A zone from a name or territory id (null: the current one), with its default map. Names match a zone's place name, exactly first;
    /// field zones and cities win over instances that share a name.
    /// </summary>
    internal static (TerritoryType Territory, Map Map) ResolveZone(string? zone)
    {
        var sheet = Svc.Data.GetExcelSheet<TerritoryType>();
        TerritoryType? found;
        if (zone is null) found = sheet.GetRowOrDefault(Svc.ClientState.TerritoryType);
        else if (uint.TryParse(zone, out var id)) found = sheet.GetRowOrDefault(id);
        else
        {
            var candidates = sheet.Where(t => t.Map.RowId != 0 && Excel.Name(t.PlaceName) is { Length: > 0 }).ToList();
            static int Rank(TerritoryType t) => t.TerritoryIntendedUse.RowId switch { 0 or 1 => 0, _ => 1 };
            var exact = candidates.Where(t => string.Equals(Excel.Name(t.PlaceName), zone, StringComparison.OrdinalIgnoreCase)).OrderBy(Rank).ThenBy(t => t.RowId).ToList();
            // A city's name ("Limsa Lominsa", "Gridania", "Ishgard") is the zone name of its districts: its first district, unless
            // a town or field zone itself has that name.
            var city = candidates.Where(t => t.TerritoryIntendedUse.RowId == 0 && string.Equals(Excel.Name(t.PlaceNameZone), zone, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(t => t.RowId).ToList();
            if (exact.Count > 0 && Rank(exact[0]) == 0) found = exact[0];
            else if (city.Count > 0) found = city[0];
            else if (exact.Count > 0) found = exact[0];
            else
            {
                var partial = candidates.Where(t => Excel.Name(t.PlaceName)!.Contains(zone, StringComparison.OrdinalIgnoreCase))
                                        .GroupBy(t => Excel.Name(t.PlaceName)!).Select(g => g.OrderBy(Rank).ThenBy(t => t.RowId).First()).OrderBy(Rank).ToList();
                if (partial.Count > 1 && partial.Count(t => Rank(t) == 0) != 1)
                    throw new ToolException($"'{zone}' matches several zones: {string.Join(", ", partial.Take(8).Select(t => Excel.Name(t.PlaceName)))}. Use the full name.");
                found = partial.FirstOrDefault();
            }
        }
        if (found is not { } territory) throw new ToolException(zone is null ? "The current zone isn't known." : $"No zone called '{zone}'.");
        if (territory.Map.ValueNullable is not { } map || map.RowId == 0) throw new ToolException($"{Excel.Name(territory.PlaceName)} has no map.");
        return (territory, map);
    }

    /// <summary>Ground distance from the player to a spot, in yalms (null if the player is elsewhere).</summary>
    private static double? Distance(uint territory, double worldX, double worldZ) =>
        Svc.ClientState.TerritoryType == territory && Svc.Objects.LocalPlayer is { } p
            ? Math.Round(MapMath.GroundDistance(p.Position.X, p.Position.Z, worldX, worldZ), 1) : null;

    /// <summary>The map flag, if one is set: where it is and how far away.</summary>
    private static unsafe object? Flag(uint here, Vector3 position)
    {
        var agent = AgentMap.Instance();
        if (agent is null || agent->FlagMarkerCount == 0) return null;
        var flag = agent->FlagMapMarkers[0];
        var territory = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(flag.TerritoryId);
        var map = Svc.Data.GetExcelSheet<Map>().GetRowOrDefault(flag.MapId);
        return new
        {
            zone = territory is { } t ? Excel.Name(t.PlaceName) : null,
            territoryId = flag.TerritoryId,
            coordinates = map is { } m ? Coords(m, flag.XFloat, flag.YFloat) : null,
            distance = here == flag.TerritoryId ? Math.Round(MapMath.GroundDistance(position.X, position.Z, flag.XFloat, flag.YFloat), 1) : (double?)null,
        };
    }

    private sealed record Spot(uint Territory, double X, double Z, string Describe);

    /// <summary>Where wait_until_arrived waits for: the given zone and coordinates, or the map flag.</summary>
    private static unsafe Spot Target(ToolArgs args)
    {
        if (args.Float("x") is null && args.Float("y") is null)
        {
            var agent = AgentMap.Instance();
            if (agent is null || agent->FlagMarkerCount == 0) throw new ToolException("No coordinates given and no flag on the map. Pass x and y, or set a flag first.");
            var flag = agent->FlagMapMarkers[0];
            var name = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(flag.TerritoryId) is { } ft ? Excel.Name(ft.PlaceName) : $"zone {flag.TerritoryId}";
            return new Spot(flag.TerritoryId, flag.XFloat, flag.YFloat, $"the flag in {name}");
        }
        var (x, y) = MapCoordinates(args);
        var (territory, map) = ResolveZone(args.String("zone"));
        var (wx, wz) = MapMath.ToWorld(Info(map), x, y);
        return new Spot(territory.RowId, wx, wz, $"{Excel.Name(territory.PlaceName)} ({x:0.0}, {y:0.0})");
    }
}
