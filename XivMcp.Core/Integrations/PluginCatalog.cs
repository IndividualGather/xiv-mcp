using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Integrations;

/// <summary>A plugin XIV MCP works with, and where to install it from: <see cref="PluginCatalog.Official"/> or a repo.json URL.</summary>
public sealed record KnownPlugin(string InternalName, string Name, string Repo);

/// <summary>
/// The plugins XIV MCP's own tools use, with the repository each one is installed from (taken from the plugins' Dalamud manifests).
/// Used to tell the player how to install a plugin a tool needs. Generated into the docs, so keep one entry per line.
/// </summary>
public static class PluginCatalog
{
    /// <summary>Dalamud's main plugin repository, which needs no setup.</summary>
    public const string Official = "official";

    public static IReadOnlyList<KnownPlugin> All { get; } =
    [
        new("vnavmesh", "vnavmesh", "https://puni.sh/api/repository/veyn"),
        new("Lifestream", "Lifestream", "https://raw.githubusercontent.com/NightmareXIV/MyDalamudPlugins/main/pluginmaster.json"),
        new("AutoDuty", "AutoDuty", "https://puni.sh/api/repository/erdelf"),
        new("Artisan", "Artisan", "https://love.puni.sh/ment.json"),
        new("GatherBuddyReborn", "GatherBuddy Reborn", "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json"),
        new("ItemVendorLocation", "Item Vendor Location", Official),
        new("FCCH", "FCCH", "https://puni.sh/api/repository/nexai"),
        new("PennyPincher", "Penny Pincher", Official),
        new("WaymarkPresetPlugin", "Waymark Preset Plugin", Official),
    ];

    public static KnownPlugin? Find(string internalName) =>
        All.FirstOrDefault(p => p.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));
}
