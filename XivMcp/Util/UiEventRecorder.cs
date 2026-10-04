using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XivMcp.Util;

/// <summary>
/// Dev builds only (capture_ui_events): records what game windows send while the player clicks — callbacks with their values
/// (AtkUnitBase.FireCallback) and component events (ReceiveEvent) — so a window's commands can be reproduced by a tool. Hooks only
/// while a recording runs.
/// </summary>
internal static unsafe class UiEventRecorder
{
    internal sealed record Entry(double Seconds, string Addon, string Kind, string Detail);

    private delegate bool FireCallbackDelegate(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close);

    private static readonly object Sync = new();
    private static readonly List<Entry> Entries = [];
    private static Hook<FireCallbackDelegate>? hook;
    private static HashSet<string>? filter;
    private static DateTime started;

    public static bool Recording { get; private set; }

    /// <summary>Starts recording the named windows (all when empty). Framework thread.</summary>
    public static void Start(IReadOnlyCollection<string> addons)
    {
        Stop();
        lock (Sync)
        {
            Entries.Clear();
            filter = addons.Count > 0 ? new HashSet<string>(addons, StringComparer.OrdinalIgnoreCase) : null;
            started = DateTime.UtcNow;
        }
        hook ??= Svc.GameInterop.HookFromAddress<FireCallbackDelegate>(AtkUnitBase.Addresses.FireCallback.Value, OnFireCallback);
        hook.Enable();
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, OnReceiveEvent);
        Recording = true;
    }

    /// <summary>Stops recording and returns what was recorded. Framework thread.</summary>
    public static List<Entry> Stop()
    {
        if (Recording)
        {
            hook?.Disable();
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, OnReceiveEvent);
            Recording = false;
        }
        lock (Sync) return [.. Entries];
    }

    public static void Dispose()
    {
        Stop();
        hook?.Dispose();
        hook = null;
    }

    private static void Add(string addon, string kind, string detail)
    {
        lock (Sync)
        {
            if (filter is not null ? !filter.Contains(addon) : addon.StartsWith('_')) return; // HUD windows only when named
            if (Entries.Count < 3000) Entries.Add(new Entry(Math.Round((DateTime.UtcNow - started).TotalSeconds, 2), addon, kind, detail));
        }
    }

    private static bool OnFireCallback(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close)
    {
        try
        {
            var name = addon == null ? "?" : addon->NameString;
            var parts = new List<string>();
            for (var i = 0; i < valueCount && i < 16; i++) parts.Add(Describe(values[i]));
            Add(name, "callback", $"[{string.Join(", ", parts)}]{(close ? " close" : "")}");
        }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] UI recorder: {ex.Message}"); }
        return hook!.Original(addon, valueCount, values, close);
    }

    private static void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs e) return;
        // Animation, timers, hovering and focus fire constantly and say nothing about what the player did.
        var t = e.AtkEventType.ToString();
        if (t.StartsWith("Timeline") || t.StartsWith("Timer") || t.StartsWith("MouseOver") || t.StartsWith("MouseOut") || t.StartsWith("MouseMove")
            || t.StartsWith("MouseWheel") || t.StartsWith("Focus")) return;
        Add(args.AddonName, "event", $"type {e.AtkEventType}, param {e.EventParam}");
    }

    private static string Describe(AtkValue v) => v.Type switch
    {
        AtkValueType.Int => $"int {v.Int}",
        AtkValueType.UInt => $"uint {v.UInt}",
        AtkValueType.Bool => $"bool {v.Byte != 0}",
        AtkValueType.Float => $"float {v.Float}",
        AtkValueType.String or AtkValueType.ManagedString
            or AtkValueType.String8 => $"string \"{(v.String.HasValue ? v.String.ToString() : "")}\"",
        _ => v.Type.ToString(),
    };
}
