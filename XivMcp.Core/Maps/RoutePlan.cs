using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace XivMcp.Maps;

/// <summary>A point on a route: a zone (name or territory id; null = the zone the player is in then) and map coordinates.</summary>
public sealed record Waypoint(string? Zone, double X, double Y, string? Name, double? Radius);

/// <summary>A job step, as the job manager runs it.</summary>
public sealed record RouteStep(string Id, string Tool, JsonObject Args, string Note);

/// <summary>
/// A route as a job: for each waypoint, place the map flag there and wait until the player arrives, then move on to the next. The flag
/// is cleared at the end. Each step is an ordinary tool call, so the job can be paused, resumed, extended or changed like any other.
/// </summary>
public static class RoutePlan
{
    public const int MaxWaypoints = 50;

    public static IReadOnlyList<RouteStep> Steps(IReadOnlyList<Waypoint> waypoints, double radius, int timeoutMinutes, bool openMap)
    {
        if (waypoints.Count == 0) throw new ArgumentException("A route needs at least one waypoint.");
        if (waypoints.Count > MaxWaypoints) throw new ArgumentException($"A route can have at most {MaxWaypoints} waypoints.");
        var steps = new List<RouteStep>();
        for (var i = 0; i < waypoints.Count; i++)
        {
            var w = waypoints[i];
            if (w.X is < 1 or > 42 || w.Y is < 1 or > 42)
                throw new ArgumentException($"Waypoint {i + 1} ({w.X}, {w.Y}) is off the map: map coordinates run from 1 to 42.");
            var note = $"Waypoint {i + 1} of {waypoints.Count}" + (string.IsNullOrWhiteSpace(w.Name) ? "" : $": {w.Name}");
            JsonObject Where()
            {
                var args = new JsonObject();
                if (!string.IsNullOrWhiteSpace(w.Zone)) args["zone"] = w.Zone;
                args["x"] = w.X;
                args["y"] = w.Y;
                return args;
            }
            var flag = Where();
            flag["open_map"] = openMap;
            if (!string.IsNullOrWhiteSpace(w.Name)) flag["name"] = w.Name;
            steps.Add(new RouteStep($"flag{i + 1}", "set_map_flag", flag, note));
            var reach = Where();
            reach["radius"] = w.Radius ?? radius;
            reach["timeout_minutes"] = timeoutMinutes;
            steps.Add(new RouteStep($"reach{i + 1}", "wait_until_arrived", reach, note));
        }
        steps.Add(new RouteStep("done", "clear_map_flag", new JsonObject(), "Route finished"));
        return steps;
    }

    /// <summary>Waypoints from a JSON array: [{ "zone"?, "x", "y", "name"?, "radius"? }].</summary>
    public static IReadOnlyList<Waypoint> Parse(JsonNode node)
    {
        if (node is not JsonArray array) throw new ArgumentException("'waypoints' must be an array.");
        return array.Select((n, i) =>
        {
            if (n is not JsonObject o || o["x"] is null || o["y"] is null) throw new ArgumentException($"Waypoint {i + 1} needs 'x' and 'y' (map coordinates).");
            return new Waypoint(ZoneText(o["zone"]), o["x"]!.GetValue<double>(), o["y"]!.GetValue<double>(), o["name"]?.GetValue<string>(),
                                o["radius"]?.GetValue<double>());
        }).ToList();
    }

    /// <summary>A zone may be given as a name or a territory id.</summary>
    private static string? ZoneText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.TryGetValue(out long id) => id.ToString(),
        _ => node.ToJsonString(),
    };
}
