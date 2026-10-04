using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Integrations;

/// <summary>
/// How much a tool depends on a plugin: <see cref="Needed"/> means the tool (or the use named in the purpose) fails without it,
/// <see cref="Improves"/> that the tool works without it and only does better with it.
/// </summary>
public enum Need { Needed, Improves }

/// <summary>A plugin one of XIV MCP's tools uses, and what for (one sentence for the player and the plugin developer).</summary>
public sealed record ToolRequirement(string PluginId, Need Need, string Purpose);

/// <summary>
/// Which of XIV MCP's own tools use other plugins. Integration tools (<see cref="IntegrationCatalog"/>) always need their plugin;
/// <see cref="Core"/> lists the core tools that use a plugin as a backend. Third-party plugins that declare these tools as
/// dependencies are told when a needed plugin is missing, and the list is generated into the docs.
/// </summary>
public static class ToolRequirements
{
    private const Need N = Need.Needed, I = Need.Improves;

    public static IReadOnlyDictionary<string, ToolRequirement[]> Core { get; } = new Dictionary<string, ToolRequirement[]>
    {
        ["navigate_to"] =
        [
            new("vnavmesh", N, "Walks to the destination when it is not in sight."),
            new("Lifestream", N, "Travels to other zones, your house or apartment, an inn or the workshop."),
        ],
        ["buy_item"] =
        [
            new("ItemVendorLocation", N, "Finds the vendors that sell the item, their prices and where they stand."),
            new("vnavmesh", N, "Walks to the vendor when it is not in sight."),
            new("Lifestream", N, "Travels to the vendor's zone."),
        ],
        ["turn_in_collectables"] =
        [
            new("ItemVendorLocation", N, "Finds where a Collectable Appraiser stands when none is nearby."),
            new("vnavmesh", N, "Walks to the appraiser when it is not in sight."),
            new("Lifestream", N, "Travels to the appraiser's zone."),
        ],
        ["place_waymark_preset"] = [new("WaymarkPresetPlugin", N, "Places presets from its library; the game's own preset slots work without it.")],
        ["list_waymark_presets"] = [new("WaymarkPresetPlugin", I, "Also lists the presets in its library.")],
        ["get_waymark_preset"] = [new("WaymarkPresetPlugin", I, "Can also read presets from its library.")],
        ["sell_item"] = [new("PennyPincher", I, "Undercuts with its settings (amount, rounding, minimum) instead of by 1 gil.")],
        ["reprice_listings"] = [new("PennyPincher", I, "Undercuts with its settings (amount, rounding, minimum) instead of by 1 gil.")],
    };

    /// <summary>The plugins a tool of XIV MCP uses (empty for tools that need none, and for tools XIV MCP doesn't know).</summary>
    public static IReadOnlyList<ToolRequirement> For(string tool)
    {
        if (IntegrationCatalog.For(tool) is { } integration)
            return [new ToolRequirement(integration.PluginId, Need.Needed, $"Does the work: {integration.Summary}")];
        return Core.TryGetValue(tool, out var needs) ? needs : [];
    }

    /// <summary>Every tool of XIV MCP that uses another plugin: the core ones listed here and all integration tools.</summary>
    public static IEnumerable<string> Tools => Core.Keys.Concat(IntegrationCatalog.All.SelectMany(i => i.Tools.Keys));
}
