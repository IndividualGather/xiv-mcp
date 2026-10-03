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

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}
