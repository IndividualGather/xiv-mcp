using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Integrations;

/// <summary>Whether a plugin is loaded, installed but turned off, or not installed.</summary>
public enum PluginInstall { Loaded, Off, Missing }

/// <summary>
/// Turning on the plugins a tool needs when they are installed but off (the player allows it in the Plugin management settings):
/// the plugins <see cref="ToolRequirements"/> lists for the tool, its own plugin and the ones that do a part of its work, so the task
/// runs by itself. Of plugins that stand in for each other, one is enough.
/// </summary>
public static class OnDemandPlugins
{
    /// <summary>
    /// Core tools that only start another plugin's automation, which <see cref="ToolRequirements.Core"/> doesn't list, as its core
    /// tools work without any plugin.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> CoreNeeds { get; } = new Dictionary<string, string[]>
    {
        ["run_autoretainer"] = ["AutoRetainer"],
    };

    /// <summary>The plugins to turn on before the tool runs, in the order it lists them.</summary>
    public static IReadOnlyList<string> ToTurnOn(string tool, Func<string, PluginInstall> state)
    {
        var groups = ToolRequirements.For(tool).Select(r => (IReadOnlyList<string>)[r.PluginId, .. r.Alternatives])
            .Concat(CoreNeeds.TryGetValue(tool, out var needs) ? needs.Select(n => (IReadOnlyList<string>)[n]) : []);
        var result = new List<string>();
        foreach (var group in groups)
        {
            if (group.Any(p => state(p) == PluginInstall.Loaded || result.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            if (group.FirstOrDefault(p => state(p) == PluginInstall.Off) is { } off) result.Add(off);
        }
        return result;
    }
}

/// <summary>
/// The plugins XIV MCP turned on for a task, and when they go off again: once no tool call has run and no job has been unfinished for
/// <see cref="IdleBefore"/>. A task can be a job that runs for hours, and the next call often follows a moment after the last one.
/// Plugins the player had on are never listed, so they stay on.
/// </summary>
public sealed class PluginLeases
{
    public static readonly TimeSpan IdleBefore = TimeSpan.FromSeconds(60);

    private readonly List<string> turnedOn = [];
    private readonly object sync = new();
    private DateTime lastBusy = DateTime.MinValue;

    public IReadOnlyList<string> TurnedOn { get { lock (sync) return turnedOn.ToList(); } }

    public void Add(string plugin, DateTime now)
    {
        lock (sync)
        {
            if (!turnedOn.Contains(plugin, StringComparer.OrdinalIgnoreCase)) turnedOn.Add(plugin);
            lastBusy = now;
        }
    }

    /// <summary>The plugins to turn off now (they are forgotten then), given whether a call or a job is running.</summary>
    public IReadOnlyList<string> Due(bool busy, DateTime now)
    {
        lock (sync)
        {
            if (busy) lastBusy = now;
            if (busy || turnedOn.Count == 0 || now - lastBusy < IdleBefore) return [];
            var due = turnedOn.ToList();
            turnedOn.Clear();
            return due;
        }
    }
}

/// <summary>
/// A Dalamud plugin collection: its name, whether it is on, whether it is the default collection (always on) and the plugins it
/// turns on.
/// </summary>
public sealed record PluginCollection(string Name, bool Enabled, bool IsDefault, IReadOnlyList<string> Plugins)
{
    public static PluginCollection Find(IReadOnlyList<PluginCollection> all, string name) =>
        all.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"There is no plugin collection named '{name}'. Collections: {string.Join(", ", all.Select(c => c.Name))}.");

    /// <summary>
    /// Why the collection can't be turned on or off, or null: the default collection is always on, and turning off the only enabled
    /// collection that wants <paramref name="self"/> would unload XIV MCP in the middle of the request.
    /// </summary>
    public static string? Refusal(PluginCollection target, bool enable, IReadOnlyList<PluginCollection> all, string self)
    {
        if (target.IsDefault) return "The default collection is always on; it can't be turned on or off.";
        if (enable || !target.Wants(self)) return null;
        return all.Any(c => c != target && c.Enabled && c.Wants(self))
            ? null
            : $"Turning off '{target.Name}' would unload XIV MCP itself, which no other enabled collection keeps on.";
    }

    private bool Wants(string plugin) => Plugins.Contains(plugin, StringComparer.OrdinalIgnoreCase);
}
