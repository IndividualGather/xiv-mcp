using System;
using System.Collections.Generic;
using System.Linq;
using XivMcp.Mcp;

namespace XivMcp.Api;

/// <summary>Who may own third-party tools: any installed plugin but XIV MCP itself, by its internal name.</summary>
public static class PluginOwners
{
    /// <summary>
    /// Returns the owner as installed (its exact internal name) or throws. Installed is enough: plugins register from their
    /// constructor, before Dalamud marks them as loaded.
    /// </summary>
    /// <param name="actualCaller">The plugin whose code made the call, found on the call stack (null: none could be told).</param>
    /// <param name="requireCaller">Refuse calls whose plugin can't be told (XIV MCP's IPC: a plugin may only act as itself).</param>
    public static string Validate(string owner, IEnumerable<string> installedInternalNames, string? actualCaller = null, bool requireCaller = false)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ToolException("Pass your plugin's internal name as the owner.");
        if (owner.Equals("XivMcp", StringComparison.OrdinalIgnoreCase)) throw new ToolException("'XivMcp' can't be an owner.");
        var id = installedInternalNames.FirstOrDefault(n => n.Equals(owner, StringComparison.OrdinalIgnoreCase))
                 ?? throw new ToolException($"No installed plugin has the internal name '{owner}'. Pass your plugin's InternalName (IDalamudPluginInterface.InternalName).");
        if (actualCaller is not null && !actualCaller.Equals(id, StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"This call comes from {actualCaller}, not from {id}: a plugin can only act as itself.");
        if (actualCaller is null && requireCaller)
            throw new ToolException("XIV MCP could not tell which plugin made this call; call it from your plugin's own code.");
        return id;
    }
}
