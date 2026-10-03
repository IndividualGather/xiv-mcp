using System;
using System.Security.Cryptography;
using Dalamud.Configuration;

namespace XivMcp;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Whether the MCP server should be running.</summary>
    public bool ServerEnabled { get; set; } = true;

    /// <summary>Local TCP port the server listens on (localhost only).</summary>
    public int Port { get; set; } = 37521;

    /// <summary>When true, clients must send "Authorization: Bearer &lt;token&gt;".</summary>
    public bool RequireToken { get; set; } = true;

    public string Token { get; set; } = NewToken();

    /// <summary>Allows the plugin tools: enable/disable/reload other plugins and read/write their config files.</summary>
    public bool AllowPluginManagement { get; set; }

    /// <summary>Allows the inventory action tools: /itemsort and moving items between slots and containers.</summary>
    public bool AllowInventoryActions { get; set; }

    /// <summary>Allows opening game windows and interacting with nearby objects (summoning bell, company chest, ...).</summary>
    public bool AllowGameInteraction { get; set; }

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
