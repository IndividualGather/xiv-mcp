using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using XivMcp.Mcp;
using XivMcp.Shops;

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

        /// <summary>Who may use it: "assistant" or "plugin:&lt;id&gt;" (see Caller.ApprovalOwner). Older approvals (null) are the assistant's.</summary>
        public string? Owner { get; set; }

        /// <summary>The player's answer; a request starts out waiting (older approvals have none and count as approved).</summary>
        public ApprovalAnswer Answer { get; set; }

        public long Remaining => Math.Max(0, MaxAmount - Spent);
        public ApprovalStatus Status => ApprovalState.Of(Answer, Revoked, ExpiresUtc, Remaining, DateTime.UtcNow);
        public bool Active => Status == ApprovalStatus.Active;
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

    /// <summary>Adds an approval request, waiting for the player's answer (<see cref="SetAnswer"/>).</summary>
    public static Approval Add(string purpose, uint currency, long max, List<uint> items, TimeSpan validFor, string owner = "assistant")
    {
        var a = new Approval
        {
            Id = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant(), Purpose = purpose, CurrencyId = currency, MaxAmount = max,
            Items = items, ExpiresUtc = DateTime.UtcNow + validFor, Owner = owner, Answer = ApprovalAnswer.Waiting,
        };
        lock (Sync) { Load().Add(a); Save(); }
        return Clone(a);
    }

    /// <summary>Records the player's answer. An approval's time starts when it is approved, not when it was asked.</summary>
    public static Approval? SetAnswer(string id, ApprovalAnswer answer, TimeSpan validFor)
    {
        lock (Sync)
        {
            if (Load().FirstOrDefault(x => x.Id == id) is not { Answer: ApprovalAnswer.Waiting } a) return null;
            a.Answer = answer;
            if (answer == ApprovalAnswer.Approved) a.ExpiresUtc = DateTime.UtcNow + validFor;
            Save();
            return Clone(a);
        }
    }

    public static Approval? Find(string id)
    {
        lock (Sync) return Load().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } a ? Clone(a) : null;
    }

    /// <summary>Requests still waiting when the plugin stops can't be answered any more (their popup is gone).</summary>
    public static void AbandonWaiting()
    {
        lock (Sync)
        {
            var waiting = Load().Where(a => a.Answer == ApprovalAnswer.Waiting).ToList();
            foreach (var a in waiting) a.Answer = ApprovalAnswer.Unanswered;
            if (waiting.Count > 0) Save();
        }
    }

    /// <summary>The active approval with that id, if it covers the item. Throws with a reason otherwise.</summary>
    /// <param name="owner">Who wants to use it (Caller.ApprovalOwner): an approval only works for whoever it was made for.</param>
    public static Approval Get(string id, uint itemId, string owner = "assistant")
    {
        lock (Sync)
        {
            var a = Load().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolException($"No approval '{id}'.");
            if (!(a.Owner ?? "assistant").Equals(owner, StringComparison.OrdinalIgnoreCase)) throw new ToolException($"Approval {a.Id} was given to someone else.");
            if (a.Revoked) throw new ToolException($"Approval {a.Id} was revoked in game.");
            if (a.Answer == ApprovalAnswer.Waiting) throw new ToolException($"Approval {a.Id} is still waiting for the player's answer in game; check list_approvals.");
            if (a.Answer == ApprovalAnswer.Declined) throw new ToolException($"The player declined approval {a.Id}.");
            if (a.Answer == ApprovalAnswer.Unanswered) throw new ToolException($"Approval {a.Id} was not answered in time; ask again.");
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
        ExpiresUtc = a.ExpiresUtc, CreatedUtc = a.CreatedUtc, Revoked = a.Revoked, Owner = a.Owner, Answer = a.Answer,
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
        state = ApprovalState.Name(a.Status),
    };
}
