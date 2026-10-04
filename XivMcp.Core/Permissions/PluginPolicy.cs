using System;
using System.Collections.Generic;

namespace XivMcp.Permissions;

/// <summary>What the player allows one third-party plugin to do. Off until the player enables it.</summary>
public sealed class PluginPolicy
{
    public bool Enabled { get; set; }

    /// <summary>Set by XIV MCP when a call did something it didn't declare; only the player can lift it.</summary>
    public bool Suspended { get; set; }

    /// <summary>The plugin was enabled, then changed its registration (new tools or capabilities): refused until the player consents again.</summary>
    public bool AwaitingConsent { get; set; }

    public string? SuspendReason { get; set; }
    public DateTime? SuspendedUtc { get; set; }

    /// <summary>The player decided on this plugin's registration (enabled it, kept it disabled, or looked at its page).</summary>
    public bool Reviewed { get; set; }

    /// <summary>The tools the plugin had registered at that decision; a new one asks again.</summary>
    public HashSet<string> ReviewedTools { get; set; } = new();

    /// <summary>The capabilities the plugin declared at that decision; a new one asks again.</summary>
    public HashSet<string> ReviewedCapabilities { get; set; } = new();

    /// <summary>The player's choice per capability id; capabilities not listed use <see cref="Capabilities.DefaultMode"/>.</summary>
    public Dictionary<string, PolicyMode> Modes { get; set; } = new();

    /// <summary>The effective mode: unknown capabilities are denied, and a critical one is never more than Ask.</summary>
    public PolicyMode ModeFor(string capabilityId)
    {
        if (Capabilities.Find(capabilityId) is not { } cap) return PolicyMode.Deny;
        var mode = Modes.TryGetValue(capabilityId, out var m) ? m : Capabilities.DefaultMode(cap.Risk);
        return Capabilities.IsModeAllowed(cap.Risk, mode) ? mode : PolicyMode.Ask;
    }

    public void Suspend(string reason, DateTime utc)
    {
        Suspended = true;
        SuspendReason = reason;
        SuspendedUtc = utc;
    }

    public void Lift()
    {
        Suspended = false;
        SuspendReason = null;
        SuspendedUtc = null;
    }
}

/// <summary>Where plugin policies live (the plugin's configuration in game; memory in tests).</summary>
public interface IPolicyStore
{
    /// <summary>Allow / Ask / Deny for XIV MCP's own tools, per group and access.</summary>
    CorePolicy Core { get; }

    /// <summary>The policy for a plugin, created (disabled) if there is none yet. The same instance each time, so changes stick.</summary>
    PluginPolicy Get(string pluginId);

    void Save();
}

public sealed class InMemoryPolicyStore(Dictionary<string, PluginPolicy>? policies = null, Action? save = null, CorePolicy? core = null) : IPolicyStore
{
    public CorePolicy Core { get; } = core ?? new CorePolicy();

    private readonly Dictionary<string, PluginPolicy> policies = policies ?? new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();

    public PluginPolicy Get(string pluginId)
    {
        lock (sync)
        {
            if (!policies.TryGetValue(pluginId, out var p)) policies[pluginId] = p = new PluginPolicy();
            return p;
        }
    }

    public void Save() => save?.Invoke();
}
