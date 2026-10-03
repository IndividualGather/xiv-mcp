using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace XivMcp.Util;

/// <summary>
/// Records the game's "The ... you put up for sale ... has sold" messages (retainer sale notices, also the ones shown at login) per
/// character, so sales can be reported even without visiting a summoning bell. Kept in pluginConfigs/XivMcp/sales.json.
/// </summary>
internal sealed class SalesTracker : IDisposable
{
    public sealed record SaleNotice(DateTime Utc, ulong ContentId, uint ItemId, string Item, bool Hq, int Quantity, long? Gil, string Text);

    private readonly List<SaleNotice> notices;
    private readonly string file = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "sales.json");
    private readonly object sync = new();

    public SalesTracker()
    {
        try { notices = File.Exists(file) ? JsonSerializer.Deserialize<List<SaleNotice>>(File.ReadAllText(file)) ?? [] : []; }
        catch { notices = []; }
        Svc.Chat.ChatMessage += OnChat;
    }

    public void Dispose() => Svc.Chat.ChatMessage -= OnChat;

    public List<SaleNotice> For(ulong contentId, DateTime? since)
    {
        lock (sync) return notices.Where(n => n.ContentId == contentId && (since is null || n.Utc >= since)).OrderByDescending(n => n.Utc).ToList();
    }

    private void OnChat(Dalamud.Game.Chat.IHandleableChatMessage chat)
    {
        if (chat.LogKind != XivChatType.RetainerSale) return;
        var message = chat.Message;
        try
        {
            var text = message.TextValue;
            var item = message.Payloads.OfType<ItemPayload>().FirstOrDefault();
            var gil = Regex.Match(text, @"([\d.,']+)\s*(gil|Gil|ギル)") is { Success: true } m && long.TryParse(Regex.Replace(m.Groups[1].Value, @"[.,']", ""), out var g) ? g : (long?)null;
            var qty = Regex.Match(text, @"<x(\d+)>|×\s*(\d+)|\bx(\d+)\b") is { Success: true } q && int.TryParse(q.Groups.Cast<Group>().Skip(1).First(x => x.Success).Value, out var n) ? n : 1;
            var notice = new SaleNotice(DateTime.UtcNow, Svc.PlayerState.ContentId, item?.ItemId ?? 0,
                item is not null ? Items.Name(item.ItemId) : text, item?.IsHQ ?? false, qty, gil, text);
            lock (sync)
            {
                notices.Add(notice);
                if (notices.Count > 2000) notices.RemoveRange(0, notices.Count - 2000);
                File.WriteAllText(file, JsonSerializer.Serialize(notices));
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Debug($"[MCP] Could not record a sale notice: {ex.Message}");
        }
    }
}
