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
    public static string Validate(string owner, IEnumerable<string> installedInternalNames)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ToolException("Pass your plugin's internal name as the owner.");
        if (owner.Equals("XivMcp", StringComparison.OrdinalIgnoreCase)) throw new ToolException("'XivMcp' can't be an owner.");
        return installedInternalNames.FirstOrDefault(n => n.Equals(owner, StringComparison.OrdinalIgnoreCase))
               ?? throw new ToolException($"No installed plugin has the internal name '{owner}'. Pass your plugin's InternalName (IDalamudPluginInterface.InternalName).");
    }
}
