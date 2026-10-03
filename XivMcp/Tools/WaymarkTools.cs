using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Waymark presets: the game's 30 preset slots, the currently placed waymarks, and — if WaymarkPresetPlugin is installed —
/// its preset library (read from its config, placed through its IPC). Presets can be rendered onto the duty's map.
/// </summary>
internal static class WaymarkTools
{
    private const string WaymarkPlugin = "WaymarkPresetPlugin";
    private static readonly string[] LibraryKeys = ["A", "B", "C", "D", "One", "Two", "Three", "Four"];

    private sealed record Point(int Index, float X, float Y, float Z, bool Active);

    private sealed record Preset(string Source, int Number, string? Name, ushort Duty, DateTimeOffset? Saved, List<Point> Markers)
    {
        public bool IsEmpty => Duty == 0 && Markers.All(m => !m.Active);
    }

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        void RequireEditing()
        {
            if (!config.AllowWaymarkEditing)
                throw new ToolException("Waymark preset editing is disabled. Enable \"Allow waymark preset editing\" in the XIV MCP settings window (/xivmcp) in game.");
        }
        void RequireInteraction()
        {
            if (!config.AllowGameInteraction)
                throw new ToolException("Placing waymarks needs \"Allow game interaction\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "list_waymark_presets",
            Description = "Lists waymark presets: the game's 30 preset slots (source \"game\") and, if WaymarkPresetPlugin is installed, its preset " +
                          "library (source \"library\"), with duty, active markers and save time. Filter by duty name or preset name. " +
                          "Use get_waymark_preset to see coordinates and a picture of the arena.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "source": { "type": "string", "enum": ["game", "library", "all"], "description": "Which presets (default all)." },
                    "query": { "type": "string", "description": "Filter on duty or preset name." },
                    "current_duty": { "type": "boolean", "description": "Only presets for the duty you are in (default false)." }
                  }
                }
                """,
            Handler = (args, _) => Game.Run<object?>(() =>
            {
                var source = args.String("source")?.ToLowerInvariant() ?? "all";
                var query = args.String("query");
                var currentDuty = args.Bool("current_duty", false) ? CurrentDuty() : (ushort?)null;
                var presets = new List<Preset>();
                if (source is "game" or "all") presets.AddRange(GamePresets().Where(p => !p.IsEmpty));
                if (source is "library" or "all") presets.AddRange(LibraryPresets());

                return new
                {
                    waymarkPresetPlugin = PluginCompat.IsLoaded(WaymarkPlugin) ? "installed: its library is included" : null,
                    presets = presets
                        .Where(p => currentDuty is null || p.Duty == currentDuty)
                        .Where(p => query is null || Game.Matches(p.Name, query) || Game.Matches(DutyName(p.Duty), query))
                        .Select(p => new
                        {
                            source = p.Source,
                            number = p.Number,
                            name = p.Name,
                            duty = new { id = p.Duty, name = DutyName(p.Duty) },
                            markers = string.Join(" ", p.Markers.Where(m => m.Active).Select(m => WaymarkMap.Labels[m.Index])),
                            saved = p.Saved,
                        }).ToList(),
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_waymark_preset",
            Description = "Shows one waymark preset — a game slot (source \"game\", number 1-30), a WaymarkPresetPlugin library preset (source \"library\", " +
                          "by number or name), or the waymarks currently placed (source \"current\") — with every marker's coordinates and, by default, " +
                          "a picture of the arena map with the waymarks drawn in.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "source": { "type": "string", "enum": ["game", "library", "current"], "description": "Default game." },
                    "number": { "type": "integer", "description": "Game slot 1-30, or library index (from list_waymark_presets)." },
                    "name": { "type": "string", "description": "Library preset name (alternative to number)." },
                    "image": { "type": "boolean", "description": "Include the arena picture (default true)." }
                  }
                }
                """,
            Handler = (args, _) => Game.Run<object?>(() =>
            {
                var preset = ResolvePreset(args.String("source") ?? "game", args.UInt("number"), args.String("name"));
                return WithImage(preset, args.Bool("image", true));
            }),
        };

        yield return new McpTool
        {
            Name = "set_waymark_preset",
            Description = "Writes one of the game's 30 waymark preset slots. Either copy from another preset (from_source + from_number/from_name: " +
                          "a game slot, a WaymarkPresetPlugin library preset, or \"current\" for the waymarks placed right now), and/or give markers " +
                          "explicitly as { \"A\": {\"x\":100,\"y\":0,\"z\":90}, \"1\": null, ... } (null removes a marker; omitted markers keep their value). " +
                          "duty is the duty (ContentFinderCondition id or name) the preset belongs to. Returns before/after with a picture; clear=true empties the slot. " +
                          "Requires 'Allow waymark preset editing' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "slot": { "type": "integer", "description": "Game preset slot 1-30." },
                    "from_source": { "type": "string", "enum": ["game", "library", "current"], "description": "Copy markers (and duty) from here first." },
                    "from_number": { "type": "integer" },
                    "from_name": { "type": "string" },
                    "markers": { "type": "object", "description": "Marker overrides keyed A, B, C, D, 1, 2, 3, 4; value {x,y,z} or null." },
                    "duty": { "type": "string", "description": "Duty name or ContentFinderCondition id." },
                    "dry_run": { "type": "boolean", "description": "Show the result without writing (default false)." },
                    "clear": { "type": "boolean", "description": "Empty the slot instead (default false)." }
                  },
                  "required": ["slot"]
                }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                RequireEditing();
                var slot = (int)(args.UInt("slot") ?? throw new ToolException("'slot' is required (1-30)."));
                if (slot is < 1 or > 30) throw new ToolException("Preset slots go from 1 to 30.");
                var dryRun = args.Bool("dry_run", false);
                var markerArgs = args.Node("markers") as JsonObject;
                var dutyArg = args.String("duty");

                return Game.Run<object?>(() =>
                {
                    var before = GamePresets()[slot - 1];
                    if (args.Bool("clear", false))
                    {
                        if (dryRun) return new { dryRun = true, wouldClear = Plain(before) };
                        WriteGameSlot(slot, new Preset("game", slot, null, 0, null, before.Markers.Select(m => new Point(m.Index, 0, 0, 0, false)).ToList()), clear: true);
                        Svc.Log.Information($"[MCP] Cleared waymark preset slot {slot}");
                        return new { cleared = slot, before = Plain(before) };
                    }
                    var markers = before.Markers.ToList();
                    var duty = before.Duty;

                    if (args.String("from_source") is { } fromSource)
                    {
                        var from = ResolvePreset(fromSource, args.UInt("from_number"), args.String("from_name"));
                        markers = from.Markers.ToList();
                        if (from.Duty != 0) duty = from.Duty;
                    }
                    if (markerArgs is not null)
                        foreach (var (key, value) in markerArgs)
                        {
                            var index = Array.FindIndex(WaymarkMap.Labels, l => l.Equals(key, StringComparison.OrdinalIgnoreCase));
                            if (index < 0) index = Array.FindIndex(LibraryKeys, l => l.Equals(key, StringComparison.OrdinalIgnoreCase));
                            if (index < 0) throw new ToolException($"Unknown marker '{key}'. Use A, B, C, D, 1, 2, 3, 4.");
                            markers[index] = value is JsonObject o
                                ? new Point(index, Num(o, "x"), Num(o, "y"), Num(o, "z"), true)
                                : markers[index] with { Active = false };
                        }
                    if (dutyArg is not null) duty = ResolveDuty(dutyArg);
                    if (duty == 0) throw new ToolException("The preset needs a duty: give 'duty' or copy from a preset that has one.");

                    var after = new Preset("game", slot, null, duty, DateTimeOffset.UtcNow, markers);
                    if (dryRun) return WithImage(after, true, new { dryRun = true, before = Plain(before) });

                    WriteGameSlot(slot, after);
                    Svc.Log.Information($"[MCP] Wrote waymark preset slot {slot} for duty {duty}");
                    return WithImage(GamePresets()[slot - 1], true, new { written = true, before = Plain(before) });
                });
            },
        };

        yield return new McpTool
        {
            Name = "place_waymark_preset",
            Description = "Places a waymark preset in the current duty, like loading it from the Waymarks window. Works only inside a duty and out of " +
                          "combat (the game's rule for presets). Library presets are placed through WaymarkPresetPlugin when it is installed. " +
                          "Requires 'Allow game interaction' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "source": { "type": "string", "enum": ["game", "library"], "description": "Default game." },
                    "number": { "type": "integer", "description": "Game slot 1-30 or library index." },
                    "name": { "type": "string", "description": "Library preset name." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                RequireInteraction();
                var source = args.String("source")?.ToLowerInvariant() ?? "game";
                return Game.RunLoggedIn<object?>(() =>
                {
                    EnsureCanPlace();
                    if (source == "library")
                    {
                        if (!PluginCompat.IsLoaded(WaymarkPlugin)) throw new ToolException("Library presets need WaymarkPresetPlugin.");
                        var name = args.String("name");
                        var number = args.UInt("number");
                        bool placed;
                        if (name is not null)
                            placed = Svc.PluginInterface.GetIpcSubscriber<string, bool>($"{WaymarkPlugin}.PlacePresetByName").InvokeFunc(name);
                        else if (number is { } n)
                            placed = Svc.PluginInterface.GetIpcSubscriber<int, bool>($"{WaymarkPlugin}.PlacePresetByIndex").InvokeFunc((int)n);
                        else throw new ToolException("Give 'name' or 'number' of the library preset.");
                        return new { placed, via = WaymarkPlugin, note = placed ? null : "WaymarkPresetPlugin refused (wrong duty or preset not found)." };
                    }

                    var slot = (int)(args.UInt("number") ?? throw new ToolException("Give the game slot 'number' (1-30)."));
                    if (slot is < 1 or > 30) throw new ToolException("Preset slots go from 1 to 30.");
                    var preset = GamePresets()[slot - 1];
                    if (preset.IsEmpty) throw new ToolException($"Game slot {slot} is empty.");
                    if (preset.Duty != CurrentDuty())
                        throw new ToolException($"Slot {slot} is for {DutyName(preset.Duty)}, but you are in {DutyName(CurrentDuty())}.");
                    PlaceDirect(preset);
                    return new { placed = true, slot, duty = DutyName(preset.Duty), markers = string.Join(" ", preset.Markers.Where(m => m.Active).Select(m => WaymarkMap.Labels[m.Index])) };
                });
            },
        };
    }

    // ------------------------------------------------------------------ reading

    private static unsafe List<Preset> GamePresets()
    {
        var list = new List<Preset>();
        var module = FieldMarkerModule.Instance();
        if (module == null) return list;
        for (var i = 0; i < module->Presets.Length; i++)
        {
            ref var p = ref module->Presets[i];
            var points = new List<Point>();
            for (var m = 0; m < 8; m++)
                points.Add(new Point(m, p.Markers[m].X / 1000f, p.Markers[m].Y / 1000f, p.Markers[m].Z / 1000f, p.IsMarkerActive(m)));
            list.Add(new Preset("game", i + 1, null, p.ContentFinderConditionId,
                p.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(p.Timestamp) : null, points));
        }
        return list;
    }

    private static unsafe Preset CurrentWaymarks()
    {
        var mc = MarkingController.Instance();
        var points = new List<Point>();
        for (var m = 0; m < 8; m++)
        {
            var f = mc->FieldMarkers[m];
            points.Add(new Point(m, f.X / 1000f, f.Y / 1000f, f.Z / 1000f, f.Active));
        }
        return new Preset("current", 0, "placed right now", CurrentDuty(), null, points);
    }

    /// <summary>WaymarkPresetPlugin's library, read from its config file (it exposes no IPC for coordinates).</summary>
    private static List<Preset> LibraryPresets()
    {
        var list = new List<Preset>();
        var file = Path.Combine(Svc.PluginInterface.ConfigFile.Directory?.FullName ?? "", WaymarkPlugin + ".json");
        if (!PluginCompat.IsLoaded(WaymarkPlugin) || !File.Exists(file)) return list;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file))?["PresetLibrary"]?["Presets"] is not JsonArray presets) return list;
            for (var i = 0; i < presets.Count; i++)
            {
                if (presets[i] is not JsonObject o) continue;
                var points = LibraryKeys.Select((key, m) => o[key] is JsonObject w
                    ? new Point(m, Num(w, "X"), Num(w, "Y"), Num(w, "Z"), w["Active"]?.GetValue<bool>() == true)
                    : new Point(m, 0, 0, 0, false)).ToList();
                DateTimeOffset? time = DateTimeOffset.TryParse(o["Time"]?.ToString(), out var t) ? t : null;
                list.Add(new Preset("library", i, o["Name"]?.ToString(), (ushort)(o["MapID"]?.GetValue<int>() ?? 0), time, points));
            }
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "Could not read the WaymarkPresetPlugin library"); }
        return list;
    }

    private static Preset ResolvePreset(string source, uint? number, string? name) => source.ToLowerInvariant() switch
    {
        "current" => CurrentWaymarks(),
        "library" => (name is not null
                         ? LibraryPresets().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                           ?? LibraryPresets().FirstOrDefault(p => Game.Matches(p.Name, name))
                         : LibraryPresets().FirstOrDefault(p => p.Number == number))
                     ?? throw new ToolException(PluginCompat.IsLoaded(WaymarkPlugin)
                         ? "No such library preset. Use list_waymark_presets."
                         : "The preset library needs WaymarkPresetPlugin."),
        "game" => number is { } n and >= 1 and <= 30 ? GamePresets()[(int)n - 1] : throw new ToolException("Give the game slot 'number' (1-30)."),
        _ => throw new ToolException("source must be game, library or current."),
    };

    // ------------------------------------------------------------------ writing / placing

    private static unsafe void WriteGameSlot(int slot, Preset preset, bool clear = false)
    {
        var module = FieldMarkerModule.Instance();
        ref var p = ref module->Presets[slot - 1];
        for (var m = 0; m < 8; m++)
        {
            var point = preset.Markers[m];
            p.Markers[m] = new GamePresetPoint { X = (int)MathF.Round(point.X * 1000), Y = (int)MathF.Round(point.Y * 1000), Z = (int)MathF.Round(point.Z * 1000) };
            p.SetMarkerActive(m, point.Active);
        }
        p.ContentFinderConditionId = preset.Duty;
        p.Timestamp = clear ? 0 : (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        module->HasChanges = true; // the game saves the preset file like after editing in the Waymarks window
    }

    /// <summary>Same safety rule WaymarkPresetPlugin uses: only in duties (content types 1-3) and out of combat.</summary>
    private static void EnsureCanPlace()
    {
        var contentType = (byte)EventFramework.GetCurrentContentType();
        if (contentType is 0 or > 3) throw new ToolException("Waymark presets can only be placed inside a duty.");
        if (Svc.Condition[ConditionFlag.InCombat]) throw new ToolException("Waymark presets can't be placed in combat.");
    }

    private static unsafe void PlaceDirect(Preset preset)
    {
        var placement = new MarkerPresetPlacement();
        for (var m = 0; m < 8; m++)
        {
            var point = preset.Markers[m];
            placement.Active[m] = point.Active;
            placement.X[m] = (int)MathF.Round(point.X * 1000);
            placement.Y[m] = (int)MathF.Round(point.Y * 1000);
            placement.Z[m] = (int)MathF.Round(point.Z * 1000);
        }
        MarkingController.Instance()->PlacePreset(&placement);
    }

    // ------------------------------------------------------------------ helpers

    private static object WithImage(Preset preset, bool image, object? extra = null)
    {
        var data = new { preset = Plain(preset), extra };
        if (!image) return data;
        // Row 0 of ContentFinderCondition exists, so "no duty" must be checked explicitly; the current waymarks use the current zone.
        var territory = preset.Duty != 0
            ? Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(preset.Duty)?.TerritoryType.RowId ?? 0
            : preset.Source == "current" ? Svc.ClientState.TerritoryType : 0;
        var markers = preset.Markers.Where(m => m.Active).Select(m => new WaymarkMap.Marker(m.Index, m.X, m.Y, m.Z)).ToList();
        if (markers.Count == 0) return new { preset = Plain(preset), extra, image = preset.Source == "current" ? "No waymarks are placed right now." : "The preset has no active waymarks." };
        var rendered = territory != 0 ? WaymarkMap.Render(territory, markers) : null;
        if (rendered is null) return new { preset = Plain(preset), extra, image = "No map available for this duty." };
        return new ToolResultWithImages(new { preset = Plain(preset), extra, map = rendered.MapName, yalmsAcrossImage = rendered.YalmsAcross },
            [new ToolImage(rendered.Png, $"{DutyName(preset.Duty) ?? rendered.MapName}: {rendered.Note}")]);
    }

    private static object Plain(Preset p) => new
    {
        source = p.Source,
        number = p.Number,
        name = p.Name,
        duty = new { id = p.Duty, name = DutyName(p.Duty) },
        saved = p.Saved,
        markers = p.Markers.ToDictionary(m => WaymarkMap.Labels[m.Index],
            m => m.Active ? (object)new { x = MathF.Round(m.X, 2), y = MathF.Round(m.Y, 2), z = MathF.Round(m.Z, 2) } : null),
    };

    private static string? DutyName(ushort cfc) =>
        cfc == 0 ? null : Svc.Data.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(cfc)?.Name.ExtractText() is { Length: > 0 } n ? Game.Clean(n) : $"duty {cfc}";

    private static ushort CurrentDuty()
    {
        if (Svc.DutyState.ContentFinderCondition.RowId is > 0 and var id) return (ushort)id;
        return (ushort)(Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(Svc.ClientState.TerritoryType)?.ContentFinderCondition.RowId ?? 0);
    }

    private static ushort ResolveDuty(string duty)
    {
        if (ushort.TryParse(duty, out var id)) return id;
        var sheet = Svc.Data.GetExcelSheet<ContentFinderCondition>();
        var match = sheet.FirstOrDefault(c => Game.Clean(c.Name.ExtractText())?.Equals(duty, StringComparison.OrdinalIgnoreCase) == true);
        if (match.RowId == 0) match = sheet.FirstOrDefault(c => !c.Name.IsEmpty && Game.Matches(c.Name.ExtractText(), duty));
        return match.RowId != 0 ? (ushort)match.RowId : throw new ToolException($"No duty matches '{duty}'.");
    }

    private static float Num(JsonObject o, string key)
    {
        var node = o[key] ?? o[key.ToLowerInvariant()] ?? o[key.ToUpperInvariant()];
        return node is JsonValue v && v.TryGetValue<double>(out var d) ? (float)d : 0f;
    }
}
