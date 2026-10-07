using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin;
using XivMcp.Util;

namespace XivMcp.Api;

/// <summary>
/// Which plugin is calling XIV MCP's IPC: Dalamud's IPC doesn't say, but every plugin's code lives in its own assembly load context,
/// so the first frame on the call stack that belongs to a plugin (past XIV MCP's and Dalamud's own) tells. This keeps a plugin from
/// acting as another one through the documented API. (A plugin that reaches into XIV MCP with reflection is beyond what any in-process
/// check can stop.)
/// </summary>
internal static class IpcCaller
{
    private static readonly Assembly Own = typeof(IpcCaller).Assembly;
    private static readonly Assembly Dalamud = typeof(IDalamudPluginInterface).Assembly;
    private static readonly object Lock = new();
    private static Dictionary<AssemblyLoadContext, string> byContext = new();

    /// <summary>The internal name of the plugin whose code made the current call, or null when none can be told.</summary>
    public static string? Find()
    {
        foreach (var frame in new StackTrace(1, false).GetFrames())
        {
            var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            if (assembly is null || assembly == Own || assembly == Dalamud) continue;
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            if (context is null || context == AssemblyLoadContext.Default) continue;
            if (PluginOf(context) is { } name) return name;
        }
        return null;
    }

    private static string? PluginOf(AssemblyLoadContext context)
    {
        lock (Lock)
        {
            if (byContext.TryGetValue(context, out var known)) return known;
            byContext = Map(); // a plugin loaded or reloaded since: look again
            return byContext.GetValueOrDefault(context);
        }
    }

    private static Dictionary<AssemblyLoadContext, string> Map()
    {
        var map = new Dictionary<AssemblyLoadContext, string>();
        foreach (var local in DalamudInternals.InstalledPlugins().Where(DalamudInternals.IsLoaded))
        {
            try
            {
                var instance = local.GetType().GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(local);
                if (instance is null || AssemblyLoadContext.GetLoadContext(instance.GetType().Assembly) is not { } ctx || ctx == AssemblyLoadContext.Default) continue;
                map[ctx] = DalamudInternals.InternalName(local);
            }
            catch (Exception ex) { Svc.Log.Debug($"[MCP] Could not map a plugin's load context: {ex.Message}"); }
        }
        return map;
    }
}
