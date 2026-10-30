using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Integrations;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Turns on the plugins a tool needs when they are installed but off, and off again once nothing runs any more (see
/// <see cref="OnDemandPlugins"/> and <see cref="PluginLeases"/>). Only when the player allowed it ("Turn on plugins a task needs"
/// under Plugin management). The plugins are loaded for now, not added to a collection: the player's collections stay as they are.
/// </summary>
internal sealed class PluginWakeUp : IDisposable
{
    private readonly Configuration config;
    private readonly Func<bool> jobsRunning;
    private readonly PluginLeases leases = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Timer timer;
    private int calls;

    public PluginWakeUp(Configuration config, Func<bool> jobsRunning)
    {
        this.config = config;
        this.jobsRunning = jobsRunning;
        timer = new Timer(_ => _ = TurnOffIdle(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    private bool Allowed => config.TurnOnNeededPlugins && config.AllowPluginManagement;

    /// <summary>Whether tools of this plugin are offered: it is loaded, or installed and may be turned on for them.</summary>
    public bool Usable(string plugin) => PluginCompat.IsLoaded(plugin) || (Allowed && State(plugin) == PluginInstall.Off);

    /// <summary>The tool, turning on what it needs before it runs.</summary>
    public McpTool Wrap(McpTool tool) => tool.WithHandler(handler => async (args, ct) =>
    {
        Interlocked.Increment(ref calls);
        try
        {
            if (Allowed) await TurnOn(tool.Name, ct).ConfigureAwait(false);
            return await handler(args, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref calls);
        }
    });

    private static PluginInstall State(string plugin)
    {
        if (XivMcp.Api.PluginNames.Same(plugin, Svc.PluginInterface.InternalName)) return PluginInstall.Missing;
        var found = Svc.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        return found is null ? PluginInstall.Missing : found.IsLoaded ? PluginInstall.Loaded : PluginInstall.Off;
    }

    private async Task TurnOn(string tool, CancellationToken ct)
    {
        if (OnDemandPlugins.ToTurnOn(tool, State) is not { Count: > 0 }) return;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var turnedOn = 0;
            foreach (var plugin in OnDemandPlugins.ToTurnOn(tool, State))
            {
                try
                {
                    await DalamudInternals.Load(DalamudInternals.Find(plugin)).ConfigureAwait(false);
                    leases.Add(plugin, DateTime.UtcNow);
                    turnedOn++;
                    Svc.Log.Information($"[MCP] Turned on {plugin} for {tool}");
                    await Svc.Framework.RunOnFrameworkThread(() => Svc.Chat.Print($"[XIV MCP] Turned on {plugin} for {tool}; it goes off again when the task is done."))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The tool reports what is missing as before.
                    Svc.Log.Warning($"[MCP] Could not turn on {plugin} for {tool}: {ex.GetBaseException().Message}");
                }
            }
            // A plugin registers its IPC and windows as it loads; give it a moment before the tool calls it.
            if (turnedOn > 0) await Task.Delay(1500, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task TurnOffIdle()
    {
        if (leases.TurnedOn.Count == 0) return;
        bool busy;
        try { busy = Volatile.Read(ref calls) > 0 || jobsRunning(); }
        catch { return; }
        var due = leases.Due(busy, DateTime.UtcNow);
        if (due.Count == 0) return;
        await TurnOff(due).ConfigureAwait(false);
    }

    private async Task TurnOff(System.Collections.Generic.IReadOnlyList<string> plugins)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var plugin in plugins)
                try
                {
                    await DalamudInternals.Unload(DalamudInternals.Find(plugin)).ConfigureAwait(false);
                    Svc.Log.Information($"[MCP] Turned {plugin} off again");
                    await Svc.Framework.RunOnFrameworkThread(() => Svc.Chat.Print($"[XIV MCP] Turned {plugin} off again.")).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Svc.Log.Warning($"[MCP] Could not turn {plugin} off again: {ex.GetBaseException().Message}");
                }
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        timer.Dispose();
        // XIV MCP unloads (an update, the game closing): what it turned on goes off with it, as the task ends here too.
        if (leases.Due(busy: false, DateTime.MaxValue) is { Count: > 0 } left) _ = TurnOff(left);
    }
}
