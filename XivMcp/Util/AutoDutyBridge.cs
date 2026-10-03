using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// AutoDuty access. Running, stopping and the scalar loop settings go through its IPC (Run / Stop / PushConfigOverrides, which it
/// reverts on PopConfigOverrides and never saves while active). Two things have no IPC and are read or set by reflection: the list of
/// duties it has paths for, and its "stop when you have N of an item" list (StopItemQtyItemDictionary), which it checks after every
/// loop. That list is only swapped in memory while our overrides are active, so AutoDuty doesn't save it, and it is put back after.
/// May need an update when AutoDuty changes these internals.
/// </summary>
internal static class AutoDutyBridge
{
    public const string InternalName = "AutoDuty";
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static bool Loaded => PluginCompat.IsLoaded(InternalName);

    public static void Require()
    {
        if (!Loaded) throw new ToolException("AutoDuty is not installed or not loaded.");
    }

    public sealed record Duty(uint TerritoryType, uint ContentFinderCondition, string Name, int Level, int ItemLevel, uint ExVersion, string[] Modes, bool HasPath);

    // ---- IPC ----

    public static void Run(uint territoryType, int loops) =>
        Svc.PluginInterface.GetIpcSubscriber<uint, int, bool, object>("AutoDuty.Run").InvokeAction(territoryType, loops, false);

    public static void Stop() => Svc.PluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop").InvokeAction();

    public static bool IsLooping() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsLooping").InvokeFunc();

    public static bool IsStopped() => Svc.PluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc();

    public static bool ContentHasPath(uint territoryType) => Svc.PluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath").InvokeFunc(territoryType);

    public static string? GetConfig(string name)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<string, string>("AutoDuty.GetConfig").InvokeFunc(name); }
        catch { return null; }
    }

    /// <summary>Applies one temporary setting; false if AutoDuty doesn't know it (older/newer version) or rejects the value.</summary>
    public static bool Override(string name, string value)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<object, bool>("AutoDuty.PushConfigOverrides").InvokeFunc(new Dictionary<string, string> { [name] = value }); }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] AutoDuty override {name}={value} failed: {ex.Message}"); return false; }
    }

    /// <summary>Reverts every override pushed since the last pop.</summary>
    public static void PopOverrides()
    {
        try { Svc.PluginInterface.GetIpcSubscriber<bool>("AutoDuty.PopConfigOverrides").InvokeFunc(); }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] AutoDuty PopConfigOverrides failed: {ex.Message}"); }
    }

    // ---- Reflection ----

    private static Assembly Asm()
    {
        Require();
        var local = DalamudInternals.Find(InternalName);
        var instance = local.GetType().GetField("instance", All)?.GetValue(local)
                       ?? throw new ToolException("Could not reach AutoDuty's plugin instance (Dalamud internals changed?).");
        return instance.GetType().Assembly;
    }

    private static Type T(Assembly asm, string name) =>
        asm.GetType(name) ?? throw new ToolException($"AutoDuty type {name} not found; this AutoDuty version is not supported.");

    private static object? Member(object? target, Type type, string name) =>
        type.GetField(name, All)?.GetValue(target) ?? type.GetProperty(name, All)?.GetValue(target);

    private static object Plugin(Assembly asm) =>
        Member(null, T(asm, "AutoDuty.AutoDuty"), "Plugin") ?? throw new ToolException("AutoDuty is not initialised yet.");

    private static object Configuration(Assembly asm) =>
        Member(null, T(asm, "AutoDuty.AutoDuty"), "Configuration") ?? throw new ToolException("AutoDuty's configuration is not available.");

    /// <summary>Every duty AutoDuty knows, with the duty modes it can run it in. Framework thread.</summary>
    public static List<Duty> Duties()
    {
        var asm = Asm();
        var dict = Member(null, T(asm, "AutoDuty.Helpers.ContentHelper"), "DictionaryContent") as IDictionary
                   ?? throw new ToolException("AutoDuty's duty list is not available.");
        var duties = new List<Duty>();
        foreach (var content in dict.Values)
        {
            if (content is null) continue;
            var t = content.GetType();
            var territory = Convert.ToUInt32(Member(content, t, "TerritoryType"));
            var modes = Member(content, t, "DutyModes")?.ToString() ?? "";
            duties.Add(new Duty(territory, Convert.ToUInt32(Member(content, t, "ContentFinderCondition")),
                Member(content, t, "EnglishName") as string ?? Member(content, t, "Name") as string ?? $"Territory {territory}",
                Convert.ToInt32(Member(content, t, "ClassJobLevelRequired")), Convert.ToInt32(Member(content, t, "ItemLevelRequired")),
                Convert.ToUInt32(Member(content, t, "ExVersion")),
                modes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(m => m != "None").ToArray(),
                ContentHasPath(territory)));
        }
        return duties;
    }

    public sealed record State(bool Looping, bool Stopped, string? Stage, int? CurrentLoop, int? LoopTimes, string? Action, string? Duty, uint? DutyTerritory);

    /// <summary>What AutoDuty is doing right now. Framework thread.</summary>
    public static State Status()
    {
        var looping = IsLooping();
        var stopped = IsStopped();
        try
        {
            var asm = Asm();
            var plugin = Plugin(asm);
            var t = plugin.GetType();
            var content = Member(plugin, t, "CurrentTerritoryContent");
            var ct = content?.GetType();
            var loops = GetConfig("LoopTimes");
            return new State(looping, stopped, Member(plugin, t, "Stage")?.ToString(), Member(plugin, t, "currentLoop") as int? ?? Member(plugin, t, "CurrentLoop") as int?,
                int.TryParse(loops, out var l) ? l : null, Member(plugin, t, "action") as string,
                content is null ? null : Member(content, ct!, "EnglishName") as string ?? Member(content, ct!, "Name") as string,
                content is null ? null : Convert.ToUInt32(Member(content, ct!, "TerritoryType")));
        }
        catch (ToolException) { throw; }
        catch { return new State(looping, stopped, null, null, null, null, null, null); }
    }

    /// <summary>
    /// Replaces AutoDuty's item stop list in memory and returns the previous entries (to hand back to <see cref="RestoreStopItems"/>).
    /// Only call while overrides are pushed, so AutoDuty won't save the temporary list. Framework thread.
    /// </summary>
    public static List<DictionaryEntry> SetStopItems(IReadOnlyDictionary<uint, int> targets)
    {
        var dict = StopItemDictionary();
        var previous = new List<DictionaryEntry>();
        for (var e = dict.GetEnumerator(); e.MoveNext();) previous.Add(e.Entry);
        dict.Clear();
        var pair = dict.GetType().GetGenericArguments()[1]; // KeyValuePair<string, int>
        foreach (var (item, qty) in targets)
            dict[item] = Activator.CreateInstance(pair, Items.Name(item), qty);
        return previous;
    }

    public static void RestoreStopItems(List<DictionaryEntry> previous)
    {
        var dict = StopItemDictionary();
        dict.Clear();
        foreach (var e in previous) dict[e.Key] = e.Value;
    }

    private static IDictionary StopItemDictionary()
    {
        var config = Configuration(Asm());
        return config.GetType().GetField("StopItemQtyItemDictionary", All)?.GetValue(config) as IDictionary
               ?? throw new ToolException("AutoDuty's item stop list (StopItemQtyItemDictionary) was not found; this AutoDuty version is not supported.");
    }

    /// <summary>The count AutoDuty's item stop condition uses (bags, armoury, equipped; currencies included). Framework thread.</summary>
    public static unsafe int Count(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId, false, true, true, 0);
    }
}
