using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Integrations;

/// <summary>
/// How much a tool depends on a plugin: <see cref="Needed"/> means the tool does not work without it (only integration tools),
/// <see cref="Improves"/> that the tool works without it, with the player doing that part (see <see cref="ToolRequirement.Without"/>).
/// </summary>
public enum Need { Needed, Improves }

/// <summary>
/// A plugin one of XIV MCP's tools uses: what it does for the tool, and (for core tools) what happens without it. One sentence each,
/// for the player and the plugin developer.
/// </summary>
public sealed record ToolRequirement(string PluginId, Need Need, string Purpose, string? Without = null);

/// <summary>
/// Which of XIV MCP's tools use other plugins. Integration tools (<see cref="IntegrationCatalog"/>) need their plugin. Core tools work
/// without any plugin: <see cref="Core"/> lists the plugins that do a part for them (walking, travel, finding vendors), and what
/// XIV MCP does instead when one is missing, usually asking the player and waiting. Generated into the docs, so keep the format.
/// </summary>
public static class ToolRequirements
{
    private const Need I = Need.Improves;

    private const string Walk = "Walks there for you.";
    private const string WalkWithout = "XIV MCP puts a flag on your map and waits while you walk there.";
    private const string Teleport = "Teleports you to the right zone.";
    private const string TeleportWithout = "XIV MCP tells you which aetheryte to teleport to, and waits until you are there.";
    private const string Undercut = "Undercuts with its settings (amount, rounding, minimum).";
    private const string UndercutWithout = "XIV MCP undercuts by 1 gil.";

    public static IReadOnlyDictionary<string, ToolRequirement[]> Core { get; } = new Dictionary<string, ToolRequirement[]>
    {
        ["navigate_to"] =
        [
            new("vnavmesh", I, Walk, WalkWithout),
            new("Lifestream", I, "Teleports you, and enters your house, apartment, inn room or workshop.",
                "XIV MCP tells you where to teleport or which place to enter, and waits until you are there."),
        ],
        ["buy_item"] =
        [
            new("ItemVendorLocation", I, "Finds the vendors that sell the item, their prices and where they stand.",
                "XIV MCP asks you to open a vendor's shop yourself, then buys from it."),
            new("vnavmesh", I, Walk, WalkWithout),
            new("Lifestream", I, Teleport, TeleportWithout),
        ],
        ["turn_in_collectables"] =
        [
            new("ItemVendorLocation", I, "Finds where a Collectable Appraiser stands.",
                "XIV MCP asks you to go to an appraiser, and waits until you are next to one."),
            new("vnavmesh", I, Walk, WalkWithout),
            new("Lifestream", I, Teleport, TeleportWithout),
        ],
        ["visit_world"] =
        [
            new("Lifestream", I, "Travels to the world or data center for you.",
                "XIV MCP asks you to travel there with World Visit or Data Center Travel, and waits until you arrive."),
        ],
        ["place_waymark_preset"] = [new("WaymarkPresetPlugin", I, "Also places presets from its library.", "Only the game's own preset slots.")],
        ["list_waymark_presets"] = [new("WaymarkPresetPlugin", I, "Also lists the presets in its library.", "Only the game's own preset slots.")],
        ["get_waymark_preset"] = [new("WaymarkPresetPlugin", I, "Can also read presets from its library.", "Only the game's own preset slots and the waymarks placed now.")],
        ["sell_item"] = [new("PennyPincher", I, Undercut, UndercutWithout)],
        ["reprice_listings"] = [new("PennyPincher", I, Undercut, UndercutWithout)],
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
