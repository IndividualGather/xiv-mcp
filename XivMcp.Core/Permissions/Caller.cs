using XivMcp.Mcp;

namespace XivMcp.Permissions;

/// <summary>
/// Who asked for a tool call: the player's AI assistant (through the MCP server) or another Dalamud plugin (its tool calls and
/// background jobs). Shown in approval prompts and kept in the audit log; a plugin's calls are also held to that plugin's permissions.
/// </summary>
/// <param name="Id">"assistant", "assistant:&lt;client&gt;" or "plugin:&lt;id&gt;".</param>
/// <param name="Name">As the player reads it: "your AI assistant (claude-code)" or the plugin's name.</param>
/// <param name="Plugin">The plugin, when a plugin is the caller.</param>
public sealed record Caller(string Id, string Name, ToolProvider? Plugin = null)
{
    /// <summary>The assistant, by its client's name when known.</summary>
    public static Caller Assistant(string? client = null) =>
        string.IsNullOrWhiteSpace(client) ? new("assistant", "your AI assistant") : new($"assistant:{client}", $"your AI assistant ({client})");

    public static Caller ForPlugin(ToolProvider plugin) => new($"plugin:{plugin.Id}", plugin.DisplayName, plugin);

    public bool IsPlugin => Plugin is not null;

    /// <summary>
    /// Whose standing approvals this caller may use: the assistant's (in chat or its jobs, whatever its client says) or one plugin's.
    /// </summary>
    public string ApprovalOwner => IsPlugin ? Id : "assistant";
}
