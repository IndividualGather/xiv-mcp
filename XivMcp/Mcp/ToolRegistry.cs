using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Mcp;

/// <summary>
/// All tools the server offers: the built-in ones plus those other plugins register at runtime (see Api/PluginApi.cs).
/// Thread-safe; <see cref="Changed"/> fires when tools are added or removed.
/// </summary>
public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, McpTool> tools = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<McpTool> builtIn)
    {
        foreach (var t in builtIn)
            if (!tools.TryAdd(t.Name, t)) throw new InvalidOperationException($"Duplicate tool name {t.Name}.");
    }

    /// <summary>Raised after tools were added or removed (on the thread that changed them).</summary>
    public event Action? Changed;

    public bool TryGet(string name, out McpTool tool) => tools.TryGetValue(name, out tool!);

    public bool Contains(string name) => tools.ContainsKey(name);

    public IReadOnlyCollection<McpTool> All => tools.Values.ToList();

    /// <summary>Adds a tool or replaces one with the same name and owner; false if the name belongs to someone else.</summary>
    public bool AddOrReplace(McpTool tool)
    {
        while (true)
        {
            if (tools.TryGetValue(tool.Name, out var existing))
            {
                if (existing.Owner is null || existing.Owner != tool.Owner) return false;
                if (!tools.TryUpdate(tool.Name, tool, existing)) continue;
            }
            else if (!tools.TryAdd(tool.Name, tool)) continue;
            Changed?.Invoke();
            return true;
        }
    }

    /// <summary>Removes one tool of an owner; built-in tools can't be removed.</summary>
    public bool Remove(string name, string owner)
    {
        if (!tools.TryGetValue(name, out var t) || t.Owner != owner || !tools.TryRemove(new KeyValuePair<string, McpTool>(name, t))) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every tool of an owner; returns how many.</summary>
    public int RemoveOwner(string owner)
    {
        var removed = 0;
        foreach (var t in tools.Values.Where(t => t.Owner == owner).ToList())
            if (tools.TryRemove(new KeyValuePair<string, McpTool>(t.Name, t))) removed++;
        if (removed > 0) Changed?.Invoke();
        return removed;
    }
}
