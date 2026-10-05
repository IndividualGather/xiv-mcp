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

    /// <summary>
    /// Imports ocean fishing presets (AutoHook export strings: folders or single presets) into XIV MCP's own folder for the goal,
    /// replacing what that folder held. The player's own presets and folders are left alone. Returns how many presets it holds now.
    /// </summary>
    public static int ImportPresets(OceanGoal goal, IReadOnlyList<string> exports)
    {
        var presets = Plugin.Get(Config, "HookPresets") ?? throw Plugin.Unsupported("Configuration.HookPresets");
        var name = OceanPresetPage.FolderName(goal);
        IEnumerable<object> Folders() => ((System.Collections.IEnumerable)(Plugin.Get(presets, "Folders") ?? throw Plugin.Unsupported("FishingPresets.Folders"))).Cast<object>();
        IEnumerable<object> Presets() => ((System.Collections.IEnumerable)(Plugin.Get(presets, "CustomPresets") ?? throw Plugin.Unsupported("FishingPresets.CustomPresets"))).Cast<object>();
        Guid Id(object o) => (Guid)Plugin.Get(o, "UniqueId")!;
        Guid? Parent(object folder) => (Guid?)Plugin.Get(folder, "ParentFolderId");

        foreach (var old in Folders().Where(f => Plugin.Get(f, "FolderName") as string == name && Parent(f) is null).ToList())
            Plugin.Call(presets, "RemoveFolderWithContents", Id(old));
        Plugin.Call(presets, "AddNewFolder", name, null);
        var ours = Folders().Last(f => Plugin.Get(f, "FolderName") as string == name && Parent(f) is null);

        var folderIds = Folders().Select(Id).ToHashSet();
        var presetIds = Presets().Select(Id).ToHashSet();
        foreach (var export in exports)
        {
            var ipc = export.StartsWith("AHFOLDER", StringComparison.OrdinalIgnoreCase) ? "AutoHook.ImportAndSelectFolder" : "AutoHook.ImportAndSelectPreset";
            Svc.PluginInterface.GetIpcSubscriber<string, object>(ipc).InvokeAction(export);
        }
        // Hang what was just imported under our folder: new top-level folders, and presets that landed outside any folder.
        foreach (var folder in Folders().Where(f => !folderIds.Contains(Id(f)) && Parent(f) is null))
            Plugin.Set(folder, "ParentFolderId", (Guid?)Id(ours));
        var inFolders = Folders().SelectMany(f => (IEnumerable<Guid>)Plugin.Get(f, "PresetIds")!).ToHashSet();
        foreach (var preset in Presets().Where(p => !presetIds.Contains(Id(p)) && !inFolders.Contains(Id(p))))
            Plugin.Call(ours, "AddPreset", Id(preset));
        Plugin.CallStatic("AutoHook.Presets.Config.Configuration", "Save");
        return Presets().Count(p => !presetIds.Contains(Id(p)));
    }

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
