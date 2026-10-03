using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Standing approvals: the player approves once in game that up to an amount of one currency may be spent (optionally only on given
/// items, until a time), e.g. for a farming job, instead of approving every purchase. Purchases draw down the remaining amount by what
/// they actually cost. Kept in pluginConfigs/XivMcp/approvals.json; the player can revoke them in /xivmcp → Jobs.
/// </summary>
internal static class Approvals
{
    public sealed class Approval
    {
        public string Id { get; set; } = "";
        public string Purpose { get; set; } = "";
        public uint CurrencyId { get; set; }
        public long MaxAmount { get; set; }
        public long Spent { get; set; }
        public List<uint> Items { get; set; } = [];
        public DateTime ExpiresUtc { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public bool Revoked { get; set; }

        public long Remaining => Math.Max(0, MaxAmount - Spent);
        public bool Active => !Revoked && DateTime.UtcNow < ExpiresUtc && Remaining > 0;
    }

    private static readonly Lock Sync = new();
    private static List<Approval>? approvals;
    private static string File => Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "approvals.json");

    private static List<Approval> Load()
    {
        if (approvals is not null) return approvals;
        try { approvals = System.IO.File.Exists(File) ? JsonSerializer.Deserialize<List<Approval>>(System.IO.File.ReadAllText(File)) ?? [] : []; }
        catch { approvals = []; }
        return approvals;
    }

    private static void Save()
    {
        try
        {
            // Keep a week of history.
            approvals!.RemoveAll(a => !a.Active && DateTime.UtcNow - a.ExpiresUtc > TimeSpan.FromDays(7) && DateTime.UtcNow - a.CreatedUtc > TimeSpan.FromDays(7));
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(approvals));
        }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] Could not save approvals: {ex.Message}"); }
    }

    public static List<Approval> All()
    {
        lock (Sync) return Load().OrderByDescending(a => a.CreatedUtc).Select(Clone).ToList();
    }

    public static Approval Add(string purpose, uint currency, long max, List<uint> items, TimeSpan validFor)
    {
        var a = new Approval
        {
            Id = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant(), Purpose = purpose, CurrencyId = currency, MaxAmount = max,
            Items = items, ExpiresUtc = DateTime.UtcNow + validFor,
        };
        lock (Sync) { Load().Add(a); Save(); }
        return Clone(a);
    }

    /// <summary>The active approval with that id, if it covers the item. Throws with a reason otherwise.</summary>
    public static Approval Get(string id, uint itemId)
    {
        lock (Sync)
        {
            var a = Load().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolException($"No approval '{id}'.");
            if (a.Revoked) throw new ToolException($"Approval {a.Id} was revoked in game.");
            if (DateTime.UtcNow >= a.ExpiresUtc) throw new ToolException($"Approval {a.Id} has expired.");
            if (a.Remaining <= 0) throw new ToolException($"Approval {a.Id} is used up ({a.Spent:N0}/{a.MaxAmount:N0} {Items.Name(a.CurrencyId)}).");
            if (a.Items.Count > 0 && !a.Items.Contains(itemId)) throw new ToolException($"Approval {a.Id} doesn't cover {Items.Name(itemId)}.");
            return Clone(a);
        }
    }

    /// <summary>Records what a purchase actually cost under an approval.</summary>
    public static void Spend(string id, long amount)
    {
        lock (Sync)
        {
            var a = Load().FirstOrDefault(x => x.Id == id);
            if (a is null) return;
            a.Spent += Math.Max(0, amount);
            Save();
        }
    }

    public static void Revoke(string id)
    {
        lock (Sync)
        {
            if (Load().FirstOrDefault(x => x.Id == id) is { } a) { a.Revoked = true; Save(); }
        }
    }

    private static Approval Clone(Approval a) => new()
    {
        Id = a.Id, Purpose = a.Purpose, CurrencyId = a.CurrencyId, MaxAmount = a.MaxAmount, Spent = a.Spent, Items = [.. a.Items],
        ExpiresUtc = a.ExpiresUtc, CreatedUtc = a.CreatedUtc, Revoked = a.Revoked,
    };

    public static object Describe(Approval a) => new
    {
        id = a.Id,
        purpose = a.Purpose,
        currency = Items.Name(a.CurrencyId),
        max = a.MaxAmount,
        spent = a.Spent,
        remaining = a.Remaining,
        items = a.Items.Count == 0 ? null : a.Items.Select(Items.Name).ToList(),
        expires = a.ExpiresUtc,
        state = a.Revoked ? "revoked" : DateTime.UtcNow >= a.ExpiresUtc ? "expired" : a.Remaining <= 0 ? "used up" : "active",
    };
}
