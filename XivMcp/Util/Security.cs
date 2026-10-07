using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Permissions;

namespace XivMcp.Util;

/// <summary>Third-party plugin policies kept in the plugin configuration.</summary>
internal sealed class ConfigPolicyStore(Configuration config) : IPolicyStore
{
    public CorePolicy Core => config.CorePolicy;

    private readonly Lock sync = new();

    public PluginPolicy Get(string pluginId)
    {
        lock (sync)
        {
            if (!config.ThirdPartyPolicies.TryGetValue(pluginId, out var p)) config.ThirdPartyPolicies[pluginId] = p = new PluginPolicy();
            return p;
        }
    }

    public void Save() => config.Save();
}

/// <summary>Approvals for third-party tools go through the in-game approval window.</summary>
internal sealed class ConsentApprovalGate : IApprovalGate
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        if (request.Area is not null) return AskBuiltIn(request, ct);
        var worst = request.Capabilities.Max(c => c.Risk);
        var details = new List<string> { $"Tool: {request.Tool}" };
        details.AddRange(request.Capabilities.Select(c => $"{c.Title} ({c.Risk.ToString().ToLowerInvariant()} risk): {c.Description}"));
        if (!string.IsNullOrWhiteSpace(request.ArgsPreview) && request.ArgsPreview != "{}") details.Add($"Arguments: {request.ArgsPreview}");
        var consent = new Consent.Request($"{request.Provider.DisplayName}: {request.Summary}", details)
        {
            Deadline = DateTime.UtcNow + Timeout,
            Source = request.Caller is { } caller && !caller.StartsWith("your AI assistant", StringComparison.Ordinal)
                ? $"{caller}, using the third-party plugin {request.Provider.DisplayName}"
                : $"the third-party plugin {request.Provider.DisplayName}",
            Risk = worst,
            OfferSession = worst != RiskLevel.Critical,
            OfferAlways = request.Capabilities.Any(c => c.Risk != RiskLevel.Critical),
            Warning = worst == RiskLevel.Critical ? "This can't be undone. XIV MCP asks every time." : null,
        };
        return Consent.Ask(consent, Timeout, ct);
    }

    /// <summary>One of XIV MCP's own tools whose group is set to Ask.</summary>
    private static Task<ApprovalDecision> AskBuiltIn(ApprovalRequest request, CancellationToken ct)
    {
        var details = new List<string>
        {
            $"Tool: {request.Tool}",
            $"Permission: {request.Area}, {(request.Write ? "changes" : "reading")} (set to Ask)",
        };
        details.AddRange(request.Capabilities.Select(c => $"{c.Title}: {c.Description}"));
        if (!string.IsNullOrWhiteSpace(request.ArgsPreview) && request.ArgsPreview != "{}") details.Add($"Arguments: {request.ArgsPreview}");
        var risk = request.Capabilities.Count > 0 ? request.Capabilities.Max(c => c.Risk) : request.Write ? RiskLevel.Medium : RiskLevel.Low;
        var who = request.Provider.Trust == XivMcp.Mcp.ProviderTrust.Maintained ? $"{request.Provider.DisplayName}: " : "";
        var consent = new Consent.Request($"{who}{request.Summary}", details)
        {
            Deadline = DateTime.UtcNow + Timeout,
            Source = request.Caller ?? "your AI assistant",
            Risk = risk,
            // Critical tools ask every time; a plugin's "always" only lasts its session (the player's own setting stays).
            OfferSession = risk != RiskLevel.Critical,
            OfferAlways = risk != RiskLevel.Critical,
        };
        return Consent.Ask(consent, Timeout, ct);
    }
}

/// <summary>
/// Snapshots of what can be observed around a third-party call: character, zone, world, gil, currencies, item count and chat lines the
/// player sent. Read on the framework thread.
/// </summary>
internal sealed class GameProbe : IGameProbe, IDisposable
{
    private int chatSent;

    public GameProbe() => Svc.Chat.ChatMessage += OnChat;

    public void Dispose() => Svc.Chat.ChatMessage -= OnChat;

    private static readonly HashSet<XivChatType> PlayerChannels =
    [
        XivChatType.Say, XivChatType.Shout, XivChatType.Yell, XivChatType.Party, XivChatType.Alliance, XivChatType.FreeCompany,
        XivChatType.TellOutgoing, XivChatType.NoviceNetwork, XivChatType.PvPTeam, XivChatType.CustomEmote, XivChatType.StandardEmote,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4, XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    ];

    private void OnChat(Dalamud.Game.Chat.IHandleableChatMessage chat)
    {
        var type = chat.LogKind;
        if (!PlayerChannels.Contains(type)) return;
        var me = Svc.Objects.LocalPlayer?.Name.TextValue;
        if (type == XivChatType.TellOutgoing || (me is not null && chat.Sender.TextValue.Contains(me, StringComparison.Ordinal))) Interlocked.Increment(ref chatSent);
    }

    public GameSnapshot? Capture()
    {
        try
        {
            return Svc.Framework.IsInFrameworkUpdateThread
                ? Read()
                : Svc.Framework.RunOnFrameworkThread(Read).Wait(TimeSpan.FromSeconds(5)) is var done && done ? lastRead : null;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[MCP] Game snapshot failed: {ex.Message}");
            return null;
        }
    }

    private GameSnapshot? lastRead;

    private static readonly InventoryType[] Containers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal, InventoryType.Crystals,
        InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    private unsafe GameSnapshot Read()
    {
        var player = Svc.Objects.LocalPlayer;
        if (!Svc.ClientState.IsLoggedIn || player is null)
            return lastRead = new GameSnapshot(false, 0, 0, 0, 0, new Dictionary<uint, long>(), 0, chatSent);
        var im = InventoryManager.Instance();
        long items = 0;
        foreach (var t in Containers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
                if (c->GetInventorySlot(i) is var s && s != null && s->ItemId != 0) items += s->Quantity;
        }
        // The currency container: gil, seals, MGP, tomestones, scrips … (gil is tracked on its own).
        var currencies = new Dictionary<uint, long>();
        var money = im->GetInventoryContainer(InventoryType.Currency);
        if (money != null)
            for (var i = 0; i < money->Size; i++)
                if (money->GetInventorySlot(i) is var s && s != null && s->ItemId > 1) currencies[s->ItemId] = currencies.GetValueOrDefault(s->ItemId) + s->Quantity;
        return lastRead = new GameSnapshot(true, Svc.PlayerState.ContentId, Svc.ClientState.TerritoryType, player.CurrentWorld.RowId,
            im->GetGil(), currencies, items, chatSent);
    }
}

/// <summary>Tells the player in chat when a plugin did or asked for something it didn't declare.</summary>
internal sealed class ChatSecurityNotifier : ISecurityNotifier
{
    public event Action? Changed;

    public void Flagged(Mcp.ToolProvider provider, string tool, IReadOnlyList<SideEffect> effects, bool suspended)
    {
        var what = string.Join(" ", effects.Select(e => e.Detail));
        Svc.Log.Warning($"[MCP] {provider.DisplayName} {tool}: undeclared — {what}{(suspended ? " — plugin suspended" : "")}");
        Svc.Framework.RunOnFrameworkThread(() => Svc.Chat.PrintError(
            suspended
                ? $"[XIV MCP] Suspended {provider.DisplayName}: {tool} did something it didn't declare ({what}). Review it in /xivmcp → Third-party plugins."
                : $"[XIV MCP] {provider.DisplayName}'s {tool} did or asked for something it didn't declare ({what}). It may have been you or another plugin; see /xivmcp → Third-party plugins."));
        Changed?.Invoke();
    }
}

/// <summary>Appends audit entries to pluginConfigs/XivMcp/audit.jsonl and reads the latest back at startup.</summary>
internal static class AuditFile
{
    private static readonly Lock Sync = new();
    private static string Path => System.IO.Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "audit.jsonl");
    private const long MaxBytes = 2_000_000;

    public static void Append(AuditEntry entry)
    {
        lock (Sync)
        {
            var file = Path;
            if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
            {
                // Keep the newer half.
                var lines = File.ReadAllLines(file);
                File.WriteAllLines(file, lines.Skip(lines.Length / 2));
            }
            File.AppendAllText(file, JsonSerializer.Serialize(entry) + "\n");
        }
    }

    public static IEnumerable<AuditEntry> ReadRecent(int max)
    {
        List<AuditEntry> entries = [];
        lock (Sync)
        {
            if (!File.Exists(Path)) return entries;
            foreach (var line in File.ReadLines(Path).TakeLast(max))
                try { if (JsonSerializer.Deserialize<AuditEntry>(line) is { } e) entries.Add(e); }
                catch (JsonException) { /* a torn line */ }
        }
        return entries; // oldest first, ready to be added in order
    }
}
