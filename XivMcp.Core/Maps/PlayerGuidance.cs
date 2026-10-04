using System;

namespace XivMcp.Maps;

/// <summary>
/// What XIV MCP asks the player to do when no plugin can do it for them: walk to a flag, teleport, go to a place, open a shop. The
/// tools then wait until the player is there. Shown in chat and as a notification, so keep each one sentence or two, no contractions.
/// </summary>
public static class PlayerGuidance
{
    /// <summary>TerritoryIntendedUse of inn rooms.</summary>
    public const uint InnUse = 2;

    /// <summary>TerritoryIntendedUse of housing interiors: houses, chambers, apartments and workshops.</summary>
    public const uint HousingInteriorUse = 14;

    public static string WalkTo(string label, string zone, double mapX, double mapY) =>
        $"Walk to {label}: XIV MCP put the flag on your map in {zone} at ({mapX:0.0}, {mapY:0.0}).";

    public static string TeleportTo(string aetheryte, string zone, string? aethernetShard) => aethernetShard is null
        ? $"Teleport to {aetheryte} (in {zone})."
        : $"Teleport to {aetheryte}, then take the aethernet to {aethernetShard} (in {zone}).";

    /// <summary>Where to go, for the places travel plugins reach ("lifestream" means the preferred property, which without one is the inn).</summary>
    public static string GoTo(string destination) => destination switch
    {
        "inn" or "lifestream" => "Go to your inn room: talk to an innkeeper in any city and enter your room.",
        "fc" => "Go into your free company house.",
        "workshop" => "Go into your free company workshop.",
        "home" => "Go into your house.",
        "apartment" => "Go into your apartment.",
        _ => throw new ArgumentException($"Unknown place '{destination}'.", nameof(destination)),
    };

    /// <summary>Whether the player is in the kind of area <see cref="GoTo"/> asked for.</summary>
    public static bool Reached(string destination, uint intendedUse) =>
        destination is "inn" or "lifestream" ? intendedUse == InnUse : intendedUse == HousingInteriorUse;

    /// <summary>World Visit (same data center) or Data Center Travel (another one), as the game offers them at aetherytes.</summary>
    public static string VisitWorld(string world, string dataCenter, bool sameDataCenter) => sameDataCenter
        ? $"Travel to {world}: use World Visit at a large aetheryte."
        : $"Travel to {world} on the {dataCenter} data center: use Data Center Travel.";

    public static string OpenShop(string item) =>
        $"Talk to a vendor that sells {item} and open their shop. XIV MCP buys as soon as the shop window is open.";

    public const string FindAppraiser =
        "Go to a Collectable Appraiser and stand next to them. XIV MCP hands in your collectables once you are there.";
}
