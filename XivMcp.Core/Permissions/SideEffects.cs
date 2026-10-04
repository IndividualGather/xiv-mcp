using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Permissions;

/// <summary>What XIV MCP can observe about the game around a call: enough to notice gil, currency or items leaving, travel, chat and logouts.</summary>
public sealed record GameSnapshot(
    bool LoggedIn,
    ulong ContentId,
    uint Territory,
    uint World,
    long Gil,
    IReadOnlyDictionary<uint, long> Currencies,
    long ItemTotal,
    int ChatSent);

/// <summary>
/// One observed change. <see cref="CoveredBy"/> are the capabilities that explain it; <see cref="Undeclared"/> means the tool declared
/// none of them.
/// </summary>
public sealed record SideEffect(string Effect, string Detail, IReadOnlyList<string> CoveredBy, bool Undeclared);

/// <summary>Compares snapshots from before and after a call against what the tool declared. Gains are never side effects.</summary>
public static class SideEffectAnalyzer
{
    public static List<SideEffect> Analyze(GameSnapshot before, GameSnapshot after, IReadOnlyCollection<string> declared)
    {
        var effects = new List<SideEffect>();
        if (!before.LoggedIn) return effects;

        void Add(string effect, string detail, params string[] coveredBy) =>
            effects.Add(new SideEffect(effect, detail, coveredBy, !coveredBy.Any(declared.Contains)));

        if (!after.LoggedIn || after.ContentId != before.ContentId)
        {
            // A different character (or none): its gil and bags say nothing about this call.
            Add("logged_out_or_switched", after.LoggedIn ? "Switched to another character." : "Logged out.", Capabilities.Login);
            return effects;
        }

        if (after.Territory != before.Territory || after.World != before.World)
            Add("moved", after.World != before.World ? $"World {before.World} → {after.World}." : $"Zone {before.Territory} → {after.Territory}.",
                Capabilities.MoveCharacter, Capabilities.Combat);
        if (after.Gil < before.Gil)
            Add("spent_gil", $"{before.Gil - after.Gil:N0} gil spent.", Capabilities.SpendGil);
        foreach (var (id, had) in before.Currencies)
            if (after.Currencies.TryGetValue(id, out var has) && has < had)
                Add("spent_currency", $"{had - has:N0} of currency {id} spent.", Capabilities.SpendCurrency);
        if (after.ItemTotal < before.ItemTotal)
            Add("items_removed", $"{before.ItemTotal - after.ItemTotal:N0} items left the inventory.",
                Capabilities.MoveItems, Capabilities.TradeItems, Capabilities.DiscardItems, Capabilities.SpendCurrency);
        if (after.ChatSent > before.ChatSent)
            Add("chat_sent", $"{after.ChatSent - before.ChatSent} chat message(s) sent.", Capabilities.ChatSend);
        return effects;
    }
}
