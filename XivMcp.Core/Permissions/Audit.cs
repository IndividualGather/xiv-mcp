using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Permissions;

/// <summary>One gated call or approval request of a third-party tool.</summary>
public sealed record AuditEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public DateTime Utc { get; init; }
    public string ProviderId { get; init; } = "";
    public string ProviderName { get; init; } = "";
    public string Tool { get; init; } = "";

    /// <summary>One of XIV MCP's own tools (core or a maintained integration), not a third-party one.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>"call" for a tool call, "approval" for an approval the plugin requested during a call.</summary>
    public string Kind { get; init; } = "call";

    public string? Summary { get; init; }
    public string? ArgsPreview { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>allowed · approved · approved_session · denied (player declined) · blocked (policy) · refused (undeclared capability).</summary>
    public string Decision { get; init; } = "";

    /// <summary>ok · error · cancelled; null when the call never ran.</summary>
    public string? Outcome { get; init; }

    public string? Error { get; init; }
    public long DurationMs { get; init; }
    public bool InJob { get; init; }
    public IReadOnlyList<SideEffect> SideEffects { get; init; } = [];

    /// <summary>Something happened (or was requested) that the tool didn't declare.</summary>
    public bool Flagged { get; init; }

    /// <summary>This entry suspended the plugin.</summary>
    public bool SuspendedPlugin { get; init; }
}

/// <summary>The most recent audit entries in memory (newest first), each also handed to <c>persist</c> (a file in game).</summary>
public sealed class AuditLog(int capacity, Action<AuditEntry>? persist = null)
{
    private readonly LinkedList<AuditEntry> entries = new();
    private readonly object sync = new();

    public event Action<AuditEntry>? Added;

    public void Add(AuditEntry entry)
    {
        lock (sync)
        {
            entries.AddFirst(entry);
            while (entries.Count > capacity) entries.RemoveLast();
        }
        try { persist?.Invoke(entry); }
        catch (Exception ex) { CoreLog.Warning?.Invoke($"[MCP] Could not persist audit entry: {ex.Message}"); }
        Added?.Invoke(entry);
    }

    /// <summary>Adds an entry read back from storage (oldest first): listed, but not persisted or announced again.</summary>
    public void AddRestored(AuditEntry entry)
    {
        lock (sync)
        {
            entries.AddFirst(entry);
            while (entries.Count > capacity) entries.RemoveLast();
        }
    }

    public IReadOnlyList<AuditEntry> Recent(string? providerId = null, int max = 100)
    {
        lock (sync)
            return entries.Where(e => providerId is null || string.Equals(e.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)).Take(max).ToList();
    }
}
