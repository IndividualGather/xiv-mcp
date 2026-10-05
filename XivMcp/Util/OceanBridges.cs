using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XivMcp.OceanFishing;

namespace XivMcp.Util;

/// <summary>
/// Distant Seas, the ocean fishing overlay (no IPC): its community fish data next to its plugin, its overlay switch, and its
/// departure alarm. Framework thread.
/// </summary>
internal static class DistantSeasBridge
{
    public const string PluginId = "DistantSeas";
    private static readonly ForeignPlugin Plugin = new(PluginId, "Distant Seas");
    private static IReadOnlyList<OceanSpotData>? spots;

    public static bool Loaded => Plugin.Loaded;

    private static object Config => Plugin.StaticValue("DistantSeas.Plugin", "Configuration") ?? throw Plugin.Unsupported("DistantSeas.Plugin.Configuration");

    /// <summary>Its fish data for both routes (Data/indigo.json and Data/ruby.json), or null when the plugin is missing.</summary>
    public static IReadOnlyList<OceanSpotData>? Spots()
    {
        if (spots is not null) return spots;
        if (!Loaded) return null;
        var dir = Plugin.Directory;
        if (dir is null) return null;
        var all = new List<OceanSpotData>();
        foreach (var file in new[] { "indigo.json", "ruby.json" })
        {
            // Installed builds keep the files next to the DLL; the source tree has them under Data.
            var path = new[] { Path.Combine(dir, file), Path.Combine(dir, "Data", file) }.FirstOrDefault(File.Exists);
            if (path is null) continue;
            try { all.AddRange(OceanFishData.Parse(File.ReadAllText(path))); }
            catch (Exception ex) { Svc.Log.Warning($"[MCP] Could not read Distant Seas' {file}: {ex.Message}"); }
        }
        return spots = all.Count > 0 ? all : null;
    }

    /// <summary>Shows its overlay until disposed (in memory; its settings file is not changed).</summary>
    public static IDisposable ShowOverlay() => Plugin.Override(Config, ("ShowOverlay", true));

    public static (bool On, int Minutes) Alarm() => (Plugin.Get(Config, "AlarmEnabled") is true, Plugin.Get(Config, "AlarmMinutes") is int m ? m : 5);

    /// <summary>Turns its departure alarm on or off and saves its settings.</summary>
    public static void SetAlarm(bool on, int? minutes, bool? sound)
    {
        var config = Config;
        Plugin.Set(config, "AlarmEnabled", on);
        if (minutes is { } m) Plugin.Set(config, "AlarmMinutes", m);
        if (sound is { } s) Plugin.Set(config, "AlarmSoundEnabled", s);
        var save = config.GetType().GetMethod("Save", []) ?? throw Plugin.Unsupported("Configuration.Save()");
        save.Invoke(config, null);
    }
}

/// <summary>
/// AutoHook's ocean fishing mode (AutoOceanFish: moves to the railing, casts, switches presets per stop and spectral current),
/// which its IPC can't switch: set on its settings for the length of a voyage, never saved. Framework thread.
/// </summary>
internal static class AutoHookOcean
{
    private static readonly ForeignPlugin Plugin = new("AutoHook", "AutoHook");

    private static object Config => Plugin.StaticValue("AutoHook.Presets.Config.Configuration", "C") ?? throw Plugin.Unsupported("Configuration.C");

    /// <summary>The goals AutoHook knows (OceanFishGoalKind), by name.</summary>
    public static IReadOnlyList<string> Goals()
    {
        var type = Plugin.Assembly.GetTypes().FirstOrDefault(t => t.Name == "OceanFishGoalKind") ?? throw Plugin.Unsupported("OceanFishGoalKind");
        return Enum.GetNames(type);
    }

    /// <summary>
    /// Turns AutoOceanFish on with <paramref name="goal"/> until disposed. <paramref name="walkToRailing"/> false when XIV MCP has
    /// already placed the player at a free spot of the railing.
    /// </summary>
    public static IDisposable Enable(string goal, bool walkToRailing)
    {
        var type = Plugin.Assembly.GetTypes().FirstOrDefault(t => t.Name == "OceanFishGoalKind") ?? throw Plugin.Unsupported("OceanFishGoalKind");
        if (!Enum.TryParse(type, goal, true, out var value))
            throw new Mcp.ToolException($"AutoHook has no ocean fishing goal '{goal}'. Goals: {string.Join(", ", Enum.GetNames(type))}.");
        return Plugin.Override(Config, ("AutoOceanFish", true), ("AutoOceanFishGoal", value), ("AOF_WalkToRailing", walkToRailing));
    }
}
