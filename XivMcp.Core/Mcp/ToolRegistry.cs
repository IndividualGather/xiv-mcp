using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Mcp;

/// <summary>
/// All tools the server offers: the core ones, those of the integrations XIV MCP maintains, and those third-party plugins register at
/// runtime. Thread-safe; <see cref="Changed"/> fires when tools are added or removed. A name belongs to the provider that registered it.
/// </summary>
public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, McpTool> tools = new(StringComparer.Ordinal);

    public ToolRegistry(IEnumerable<McpTool> initial)
    {
        foreach (var t in initial)
            if (!tools.TryAdd(t.Name, t)) throw new InvalidOperationException($"Duplicate tool name {t.Name}.");
    }

    /// <summary>Raised after tools were added or removed (on the thread that changed them).</summary>
    public event Action? Changed;

    public bool TryGet(string name, out McpTool tool) => tools.TryGetValue(name, out tool!);

    public bool Contains(string name) => tools.ContainsKey(name);

    public IReadOnlyCollection<McpTool> All => tools.Values.ToList();

    /// <summary>Adds a tool or replaces one of the same provider; false if the name belongs to the core or another provider.</summary>
    public bool AddOrReplace(McpTool tool)
    {
        while (true)
        {
            if (tools.TryGetValue(tool.Name, out var existing))
            {
                if (existing.Provider.Trust == ProviderTrust.Core || existing.Provider != tool.Provider) return false;
                if (!tools.TryUpdate(tool.Name, tool, existing)) continue;
            }
            else if (!tools.TryAdd(tool.Name, tool)) continue;
            Changed?.Invoke();
            return true;
        }
    }

    /// <summary>Removes one tool of a provider; core tools can't be removed.</summary>
    public bool Remove(string name, ToolProvider provider)
    {
        if (provider.Trust == ProviderTrust.Core || !tools.TryGetValue(name, out var t) || t.Provider != provider
            || !tools.TryRemove(new KeyValuePair<string, McpTool>(name, t))) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes every tool of a provider; returns how many.</summary>
    public int RemoveProvider(ToolProvider provider)
    {
        if (provider.Trust == ProviderTrust.Core) return 0;
        var removed = 0;
        foreach (var t in tools.Values.Where(t => t.Provider == provider).ToList())
            if (tools.TryRemove(new KeyValuePair<string, McpTool>(t.Name, t))) removed++;
        if (removed > 0) Changed?.Invoke();
        return removed;
    }
}
