namespace XivMcp.Mcp;

/// <summary>How much a tool's provider is trusted, which decides how its calls are gated.</summary>
public enum ProviderTrust
{
    /// <summary>XIV MCP's own tools; gated by the core permission switches.</summary>
    Core,

    /// <summary>Integrations with other plugins that XIV MCP itself maintains (AutoDuty, Artisan, …); gated like core tools.</summary>
    Maintained,

    /// <summary>Tools other plugins register through the plugin API; gated per capability, approved by the player and audited.</summary>
    ThirdParty,
}

/// <summary>
/// The source of a set of tools. <see cref="Id"/> is the internal name of the plugin the tools belong to (for integrations: the plugin
/// they drive). Two providers are the same only if id and trust match, so a third-party plugin can't take over an integration's tools.
/// </summary>
public sealed record ToolProvider(string Id, string DisplayName, ProviderTrust Trust)
{
    public static readonly ToolProvider Core = new("XivMcp", "XIV MCP", ProviderTrust.Core);

    // Identity is id + trust; the display name may change (plugin renamed) without making it someone else.
    public bool Equals(ToolProvider? other) =>
        other is not null && Trust == other.Trust && string.Equals(Id, other.Id, System.StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => System.HashCode.Combine(Trust, Id.ToUpperInvariant());
}
