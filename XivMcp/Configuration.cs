using System;
using System.Security.Cryptography;
using Dalamud.Configuration;

namespace XivMcp;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>Whether the MCP server should be running.</summary>
    public bool ServerEnabled { get; set; } = true;

    /// <summary>Local TCP port the server listens on (localhost only).</summary>
    public int Port { get; set; } = 37521;

    /// <summary>When true, clients must send "Authorization: Bearer &lt;token&gt;".</summary>
    public bool RequireToken { get; set; } = true;

    public string Token { get; set; } = NewToken();

    /// <summary>The assistant chosen in the Connect tab: "claude", "codex" or "other".</summary>
    public string ConnectClient { get; set; } = "claude";

    /// <summary>Allows the plugin tools: enable/disable/reload other plugins and read/write their config files.</summary>
    public bool AllowPluginManagement { get; set; }

    /// <summary>Game & navigation: opening game windows, interacting with objects and NPCs, and moving the character (vnavmesh / Lifestream).</summary>
    public bool AllowGameNavigation { get; set; }

    /// <summary>UI editing: creating, editing and clearing macros and writing the waymark preset slots.</summary>
    public bool AllowUiEditing { get; set; }

    /// <summary>Items & retainers: sorting and moving items, retainer and FC chest transfers, retainer ventures.</summary>
    public bool AllowItemsRetainers { get; set; }

    /// <summary>Market & purchases: buying from NPC vendors (non-gil asks in game), selling and repricing through retainers.</summary>
    public bool AllowMarketPurchases { get; set; }

    /// <summary>Allows starting crafts / lists in Artisan, editing Artisan and GatherBuddy Reborn lists and toggling auto-gather.</summary>
    public bool AllowCraftingGathering { get; set; }

    /// <summary>Allows online lookups (FFXIV Teamcraft's item data, ffxiv.consolegameswiki.com, universalis.app prices).</summary>
    public bool AllowOnlineData { get; set; }

    /// <summary>Plugins (by internal name) whose tools, registered through the plugin API, are offered to clients. Off until the player allows a plugin.</summary>
    public System.Collections.Generic.HashSet<string> AllowedToolPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional: gil purchases costing more than this in one buy_item call also ask in game first (0 = gil never asks).</summary>
    public int AskAboveGil { get; set; }

    // ---- Permissions before version 2 (merged above). Read once from older settings files, never written again.
    public bool AllowInventoryActions { get; set; }
    public bool AllowGameInteraction { get; set; }
    public bool AllowMacroEditing { get; set; }
    public bool AllowWaymarkEditing { get; set; }
    public bool AllowNavigation { get; set; }
    public bool AllowPurchasing { get; set; }
    public bool AllowMarket { get; set; }
    public bool AllowVentures { get; set; }
    public bool ShouldSerializeAllowInventoryActions() => false;
    public bool ShouldSerializeAllowGameInteraction() => false;
    public bool ShouldSerializeAllowMacroEditing() => false;
    public bool ShouldSerializeAllowWaymarkEditing() => false;
    public bool ShouldSerializeAllowNavigation() => false;
    public bool ShouldSerializeAllowPurchasing() => false;
    public bool ShouldSerializeAllowMarket() => false;
    public bool ShouldSerializeAllowVentures() => false;

    /// <summary>
    /// Version 1 → 2: the eleven permissions were merged into seven. A merged permission starts on only if every permission it replaces
    /// was on (so nothing becomes allowed that wasn't before). Returns true if something changed and should be saved.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= 2) return false;
        AllowGameNavigation = AllowGameInteraction && AllowNavigation;
        AllowUiEditing = AllowMacroEditing && AllowWaymarkEditing;
        AllowItemsRetainers = AllowInventoryActions && AllowVentures;
        AllowMarketPurchases = AllowPurchasing && AllowMarket;
        Version = 2;
        return true;
    }

    /// <summary>Where navigate_to looks for a summoning bell when none is nearby: lifestream (its property priority), inn, fc, home, apartment.</summary>
    public string PreferredBellLocation { get; set; } = "lifestream";

    /// <summary>Pause between two item moves, so the server can confirm each one.</summary>
    public int MoveDelayMs { get; set; } = 700;

    /// <summary>When true, the pause between moves is random between <see cref="MoveDelayMinMs"/> and <see cref="MoveDelayMaxMs"/>;
    /// otherwise it is exactly <see cref="MoveDelayMs"/>.</summary>
    public bool MoveDelayRandom { get; set; } = true;

    public int MoveDelayMinMs { get; set; } = 500;
    public int MoveDelayMaxMs { get; set; } = 800;

    public const int MoveDelayLimitMin = 200;
    public const int MoveDelayLimitMax = 5000;

    /// <summary>The pause to wait after one item move before the next one.</summary>
    public TimeSpan NextMoveDelay()
    {
        if (!MoveDelayRandom) return TimeSpan.FromMilliseconds(Math.Clamp(MoveDelayMs, MoveDelayLimitMin, MoveDelayLimitMax));
        var min = Math.Clamp(Math.Min(MoveDelayMinMs, MoveDelayMaxMs), MoveDelayLimitMin, MoveDelayLimitMax);
        var max = Math.Clamp(Math.Max(MoveDelayMinMs, MoveDelayMaxMs), MoveDelayLimitMin, MoveDelayLimitMax);
        return TimeSpan.FromMilliseconds(Random.Shared.Next(min, max + 1));
    }

    public string MoveDelayDescription => MoveDelayRandom ? $"random {MoveDelayMinMs}-{MoveDelayMaxMs} ms" : $"exactly {MoveDelayMs} ms";

    /// <summary>Cached snapshots (submersibles, retainers) older than this are reported as stale with a refresh suggestion.</summary>
    public int CacheStaleHours { get; set; } = 12;

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}
