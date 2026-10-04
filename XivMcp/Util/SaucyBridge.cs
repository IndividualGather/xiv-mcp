using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Drives Saucy, which offers no IPC: its /saucy command for Triple Triad, and reflection into the running plugin to read whether a
/// run is going, its stats, and to change settings for the length of a tool call (put back afterwards). Every member is looked up
/// by name, so a Saucy update that renames one gives a clear error instead of wrong behaviour. Framework thread.
/// </summary>
internal static class SaucyBridge
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>The loaded Saucy plugin object, or null.</summary>
    private static object? Plugin()
    {
        var local = DalamudInternals.InstalledPlugins().FirstOrDefault(p => DalamudInternals.InternalName(p) == "Saucy" && DalamudInternals.IsLoaded(p));
        return local?.GetType().GetField("instance", Instance)?.GetValue(local);
    }

    public static bool Loaded => Plugin() is not null;

    private static Assembly Assembly => Plugin()?.GetType().Assembly ?? throw new ToolException("Saucy is not loaded. Install or enable it in the plugin installer.");

    private static Type Type(string fullName) =>
        Assembly.GetType(fullName) ?? throw Unsupported(fullName);

    private static ToolException Unsupported(string what) =>
        new($"This version of Saucy is not supported ({what} is missing). Update XIV MCP, or tell its developers.");

    private static object? StaticValue(string type, string member)
    {
        var t = Type(type);
        if (t.GetField(member, Static) is { } f) return f.GetValue(null);
        if (t.GetProperty(member, Static) is { } p) return p.GetValue(null);
        throw Unsupported($"{type}.{member}");
    }

    /// <summary>Saucy's settings object (Saucy.Saucy.C).</summary>
    private static object Config => StaticValue("Saucy.Saucy", "C") ?? throw Unsupported("Saucy.Saucy.C");

    // ---------------------------------------------------------------- Triple Triad state

    /// <summary>True while Saucy's Triple Triad automation is on (a run is going or about to start).</summary>
    public static bool TriadRunning => StaticValue("Saucy.TripleTriad.TriadRunSession", "ModuleEnabled") is true;

    /// <summary>Matches finished in the current run (reset to 0 when the run ends).</summary>
    public static int MatchesThisRun => StaticValue("Saucy.TripleTriad.TriadRunSession", "MatchesCompletedThisSession") is int n ? n : 0;

    /// <summary>True while Saucy's deck optimizer builds a deck.</summary>
    public static bool OptimizerBusy => StaticValue("Saucy.TripleTriad.UI.TriadDeckOptimizerJobs", "InProgressAny") is true;

    /// <summary>True while Saucy walks from the Jumbo Cactpot Cashier to the broker (with vnavmesh).</summary>
    public static bool JumboWalking => StaticValue("Saucy.JumboCactpot.JumboCactpotBrokerPath", "IsActive") is true;

    /// <summary>Saucy's lifetime Triple Triad and arcade stats.</summary>
    public static Dictionary<string, object?> Stats()
    {
        var stats = Get(Config, "Stats") ?? throw Unsupported("Configuration.Stats");
        var result = new Dictionary<string, object?>();
        foreach (var name in new[] { "GamesPlayedWithSaucy", "GamesWonWithSaucy", "GamesLostWithSaucy", "GamesDrawnWithSaucy", "MGPWon", "CardsDroppedWithSaucy" })
            result[name] = Get(stats, name);
        result["CardsWon"] = Get(stats, "CardsWon") is IDictionary won ? won.Keys.Cast<object>().ToDictionary(k => System.Convert.ToInt32(k), k => System.Convert.ToInt32(won[k])) : new Dictionary<int, int>();
        return result;
    }

    // ---------------------------------------------------------------- settings

    private static object? Get(object target, string member)
    {
        var t = target.GetType();
        if (t.GetProperty(member, Instance) is { } p) return p.GetValue(target);
        if (t.GetField(member, Instance) is { } f) return f.GetValue(target);
        throw Unsupported($"{t.Name}.{member}");
    }

    private static void Set(object target, string member, object? value)
    {
        var t = target.GetType();
        if (t.GetProperty(member, Instance) is { CanWrite: true } p) p.SetValue(target, Coerce(value, p.PropertyType));
        else if (t.GetField(member, Instance) is { } f) f.SetValue(target, Coerce(value, f.FieldType));
        else throw Unsupported($"{t.Name}.{member}");
    }

    private static object? Coerce(object? value, Type type) =>
        value is null || type.IsInstanceOfType(value) ? value : type.IsEnum ? Enum.ToObject(type, value) : System.Convert.ChangeType(value, type);

    /// <summary>
    /// Sets Saucy settings ("Member" or "Nested.Member" on its configuration) until the result is disposed, then puts the old values
    /// back. Changes stay in memory: Saucy's settings file is not touched by XIV MCP.
    /// </summary>
    public static IDisposable Override(params (string Path, object? Value)[] settings)
    {
        var undo = new List<Action>();
        try
        {
            foreach (var (path, value) in settings)
            {
                var parts = path.Split('.');
                var target = Config;
                foreach (var part in parts[..^1]) target = Get(target, part) ?? throw Unsupported(path);
                var old = Get(target, parts[^1]);
                Set(target, parts[^1], value);
                var t = target;
                undo.Add(() => Set(t, parts[^1], old));
            }
        }
        catch
        {
            foreach (var u in Enumerable.Reverse(undo)) u();
            throw;
        }
        return new Restore(undo);
    }

    /// <summary>Turns one of Saucy's modules ("MiniCactpot", "JumboCactpot", …) on until disposed; off again afterwards if it was off.</summary>
    public static IDisposable EnableModule(string module)
    {
        if (Get(Config, "EnabledModules") is not IList modules) throw Unsupported("Configuration.EnabledModules");
        if (modules.Contains(module)) return new Restore([]);
        modules.Add(module); // an observable collection: Saucy enables the module on the change
        return new Restore([() => modules.Remove(module)]);
    }

    private sealed class Restore(List<Action> undo) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done) return;
            done = true;
            // Put settings back on the framework thread, like every other access.
            Svc.Framework.RunOnFrameworkThread(() =>
            {
                foreach (var u in Enumerable.Reverse(undo))
                    try { u(); }
                    catch (Exception ex) { Svc.Log.Warning($"[MCP] Could not restore a Saucy setting: {ex.Message}"); }
            });
        }
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Runs "/saucy &lt;args&gt;" through Dalamud's command manager.</summary>
    public static void Command(string args)
    {
        if (!Svc.Commands.ProcessCommand($"/saucy {args}")) throw new ToolException("Saucy did not take the command /saucy " + args + ".");
    }
}
