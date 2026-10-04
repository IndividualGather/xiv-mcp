using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// AutoDuty access. Running and stopping go through its IPC (Run / Stop / IsLooping / IsStopped / ContentHasPath). Its settings live
/// in profiles, which have no IPC: run_duty farms with a copy of the player's default profile set up for the run (UseFarmProfile),
/// made by reflection on ConfigurationMain and removed after (EndFarmProfile). The duty list is read by reflection too. Setting names
/// for GetConfig / PushConfigOverrides are dotted paths (Meta.LoopTimes, Loop.Termination.StopItemQty). May need an update when
/// AutoDuty changes these internals.
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

    /// <summary>Continues the current duty from where the path is (not from the start).</summary>
    public static void Resume() => Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoDuty.Start").InvokeAction(false);

    public static bool Running() => IsLooping() || !IsStopped();

    public static void Stop() =>Svc.PluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop").InvokeAction();

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
            var loops = GetConfig("Meta.LoopTimes");
            return new State(looping, stopped, Member(plugin, t, "Stage")?.ToString(), Member(plugin, t, "currentLoop") as int? ?? Member(plugin, t, "CurrentLoop") as int?,
                int.TryParse(loops, out var l) ? l : null, Member(plugin, t, "action") as string,
                content is null ? null : Member(content, ct!, "EnglishName") as string ?? Member(content, ct!, "Name") as string,
                content is null ? null : Convert.ToUInt32(Member(content, ct!, "TerritoryType")));
        }
        catch (ToolException) { throw; }
        catch { return new State(looping, stopped, null, null, null, null, null, null); }
    }

    // ---- Profiles ----
    // AutoDuty keeps every setting in profiles (ConfigurationMain, one active at a time) and has no IPC for them. run_duty farms with
    // a copy of the player's default profile that carries its stop condition, switches to it for the run, and switches back after.

    /// <summary>The profile active before the run, to switch back to.</summary>
    public sealed record FarmProfile(string Previous, string Source);

    private static object Main(Assembly asm) =>
        Member(null, T(asm, "AutoDuty.Configurations.ConfigurationMain"), "Instance") ?? throw new ToolException("AutoDuty's profiles are not available.");

    private static object? Call(object target, string method, params object?[] args) =>
        (target.GetType().GetMethods(All).FirstOrDefault(m => m.Name == method && m.GetParameters().Length == args.Length)
         ?? throw new ToolException($"AutoDuty has no {method}; this AutoDuty version is not supported.")).Invoke(target, args);

    private static object Need(object? value, string what) => value ?? throw new ToolException($"AutoDuty's {what} was not found; this AutoDuty version is not supported.");

    private static void Set(object target, string name, object? value)
    {
        var t = target.GetType();
        if (t.GetProperty(name, All) is { CanWrite: true } p) p.SetValue(target, Coerce(value, p.PropertyType));
        else if (t.GetField(name, All) is { } f) f.SetValue(target, Coerce(value, f.FieldType));
        else throw new ToolException($"AutoDuty's setting {name} was not found; this AutoDuty version is not supported.");
    }

    private static object? Coerce(object? value, Type type) =>
        value is string s && type.IsEnum ? Enum.Parse(type, s, true) : value is null || type.IsInstanceOfType(value) ? value : System.Convert.ChangeType(value, type);

    private static IEnumerable<string> ProfileNames(object main) => ((IEnumerable)Need(Member(main, main.GetType(), "ConfigNames"), "profile list")).Cast<string>().ToList();

    /// <summary>Removes a profile from AutoDuty's lists without switching to it (RemoveCurrentProfile would reset to the default a tick later).</summary>
    private static void Forget(object main, string name)
    {
        var t = main.GetType();
        if (Call(main, "GetProfile", name) is not { } profile) return;
        if (Member(main, t, "profileData") is { } data) data.GetType().GetMethod("Remove")!.Invoke(data, [profile]);
        if (Member(main, t, "profileByName") is IDictionary byName) byName.Remove(name);
        if (Member(main, t, "profileByCID") is IDictionary byCid)
            foreach (var key in byCid.Keys.Cast<object>().Where(k => byCid[k] as string == name).ToList()) byCid.Remove(key);
    }

    /// <summary>
    /// Switches AutoDuty to a copy of the player's default profile (the character's, else the global one) set up for one farming run:
    /// the duty mode, unsynced, the item targets as its stop condition ("stop at item quantity"), no other stop conditions and no
    /// termination action. Kept in memory only; <see cref="EndFarmProfile"/> switches back and removes it. Framework thread.
    /// </summary>
    public static FarmProfile UseFarmProfile(string mode, bool? unsynced, IReadOnlyDictionary<uint, int> targets, bool all)
    {
        var main = Main(Asm());
        var t = main.GetType();
        foreach (var stale in ProfileNames(main).Where(XivMcp.Duties.AutoDutyProfiles.IsTemporary).ToList()) Forget(main, stale);

        var previous = (string)Need(Member(main, t, "ActiveProfileName"), "active profile");
        string? characterDefault = null;
        if (Member(main, t, "profileByCID") is IDictionary byCid && byCid.Contains(Svc.PlayerState.ContentId)) characterDefault = byCid[Svc.PlayerState.ContentId] as string;
        var source = XivMcp.Duties.AutoDutyProfiles.Source(characterDefault, Member(main, t, "DefaultConfigName") as string, previous, ProfileNames(main).ToList());

        // Copy with AutoDuty's own duplicate (its serializer does the deep copy), then register the copy under XIV MCP's name.
        if (Call(main, "SetProfile", source) is false) throw new ToolException($"AutoDuty could not switch to its profile '{source}'.");
        Call(main, "DuplicateCurrentProfile");
        var duplicate = (string)Need(Member(main, t, "ActiveProfileName"), "active profile");
        var config = Need(Member(Need(Call(main, "GetProfile", duplicate), "copied profile"), Need(Call(main, "GetProfile", duplicate), "copied profile").GetType(), "Config"), "copied settings");
        Forget(main, duplicate);
        Call(main, "CreateProfile", XivMcp.Duties.AutoDutyProfiles.Temporary, config);

        try
        {
            var meta = Need(Member(config, config.GetType(), "Meta"), "Meta settings");
            Set(meta, "DutyModeEnum", mode);
            if (unsynced is { } u) Set(meta, "Unsynced", u);
            var loop = Need(Member(config, config.GetType(), "Loop"), "Loop settings");
            var termination = Need(Member(loop, loop.GetType(), "Termination"), "termination settings");
            Set(termination, "Enabled", true);
            foreach (var other in new[] { "StopLevel", "StopNoRestedXP", "StopWhenDutyGathered", "TerminationiLvl", "TerminationBLUSpellsEnabled" })
                Set(termination, other, false);
            Set(termination, "TerminationMethodEnum", "Do_Nothing");
            Set(termination, "StopItemQty", targets.Count > 0);
            Set(termination, "StopItemAll", all);
            var stopItems = (IDictionary)Need(Member(termination, termination.GetType(), "StopItemQtyItemDictionary"), "item stop list");
            stopItems.Clear();
            var pair = stopItems.GetType().GetGenericArguments()[1]; // KeyValuePair<string, int>
            foreach (var (item, qty) in targets) stopItems[item] = Activator.CreateInstance(pair, Items.Name(item), qty);
        }
        catch
        {
            EndFarmProfile(new FarmProfile(previous, source));
            throw;
        }
        return new FarmProfile(previous, source);
    }

    /// <summary>Switches back to the profile active before the run and removes the farming copy. Framework thread; never throws.</summary>
    public static void EndFarmProfile(FarmProfile farm)
    {
        try
        {
            var main = Main(Asm());
            Call(main, "SetProfile", farm.Previous);
            Forget(main, XivMcp.Duties.AutoDutyProfiles.Temporary);
        }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] Could not switch AutoDuty back to its profile '{farm.Previous}': {ex.Message}"); }
    }

    /// <summary>The count AutoDuty's item stop condition uses (bags, armoury, equipped; currencies included). Framework thread.</summary>
    public static unsafe int Count(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId, false, true, true, 0);
    }
}
