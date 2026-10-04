using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMcp.Mcp;
using XivMcp.Tools;

namespace XivMcp.Util;

/// <summary>
/// Pressing buttons of game windows by their label (in the game's language, from the Addon text sheet), and the "Gil Transfer"
/// window (addon "Bank") that retainers and the company chest use to move gil.
/// </summary>
internal static class GameWindows
{
    /// <summary>The texts of Addon sheet rows, in the client's language, without the parts that hold numbers.</summary>
    public static IReadOnlySet<string> Texts(params uint[] rows) => rows
        .Select(r => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(r)?.Text.ExtractText().Trim())
        .Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Ready(string addon) => RetainerUi.Ready(addon);

    public static unsafe AtkUnitBase* Addon(string name) => (AtkUnitBase*)RetainerUi.Ptr(name).Address;

    /// <summary>Every text shown in a window (its own text nodes and those inside its components).</summary>
    public static unsafe List<string> AllTexts(AtkUnitBase* addon)
    {
        var texts = new List<string>();
        if (addon == null) return texts;
        var uld = &addon->UldManager;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null) continue;
            if (node->Type == NodeType.Text) Add(texts, node);
            else if ((int)node->Type >= 1000 && ((AtkComponentNode*)node)->Component is var c && c != null)
                for (var j = 0; j < c->UldManager.NodeListCount; j++)
                    if (c->UldManager.NodeList[j] is var inner && inner != null && inner->Type == NodeType.Text) Add(texts, inner);
        }
        return texts;
    }

    private static unsafe void Add(List<string> texts, AtkResNode* node)
    {
        var text = ((AtkTextNode*)node)->NodeText.ToString().Trim();
        if (text.Length > 0) texts.Add(text);
    }

    /// <summary>The first text inside a component (a button's label).</summary>
    private static unsafe string? Label(AtkComponentBase* component)
    {
        for (var j = 0; j < component->UldManager.NodeListCount; j++)
        {
            var n = component->UldManager.NodeList[j];
            if (n == null || n->Type != NodeType.Text) continue;
            var text = ((AtkTextNode*)n)->NodeText.ToString().Trim();
            if (text.Length > 0) return text;
        }
        return null;
    }

    /// <summary>Presses the first visible, enabled button (or tab) of a window whose label is one of <paramref name="labels"/>.</summary>
    public static unsafe bool Press(AtkUnitBase* addon, IReadOnlySet<string> labels)
    {
        if (addon == null) return false;
        var uld = &addon->UldManager;
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->IsVisible()) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null) continue;
            if (component->GetComponentType() is not (ComponentType.Button or ComponentType.RadioButton or ComponentType.HoldButton)) continue;
            if (Label(component) is not { } label || !labels.Contains(label)) continue;
            if (VentureTools.Click(addon, (AtkComponentButton*)component)) return true;
        }
        return false;
    }

    public static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await Game.Run(condition).ConfigureAwait(false)) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return false;
    }

    // ------------------------------------------------------------------ the Gil Transfer window

    /// <summary>"Select amount to withdraw." and "Select amount to deposit." (Addon 914 and 915): which way the window moves gil.</summary>
    private static unsafe bool? BankDeposits()
    {
        var texts = AllTexts(Addon("Bank"));
        if (texts.Any(Texts(915).Contains)) return true;
        if (texts.Any(Texts(914).Contains)) return false;
        return null;
    }

    /// <summary>
    /// Moves gil with the open Gil Transfer window: switches it to deposit or withdraw, enters the amount and confirms, as AutoRetainer
    /// drives the same window (callbacks: 2 switches the direction, 3 sets the amount, 0 confirms).
    /// </summary>
    public static async Task Bank(bool deposit, long amount, CancellationToken ct)
    {
        if (!await WaitFor(() => Ready("Bank"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException("The Gil Transfer window did not open.");
        await Task.Delay(300, ct).ConfigureAwait(false);
        if (await Game.Run(BankDeposits).ConfigureAwait(false) != deposit)
        {
            await Game.Run(() => FireBank(2, null)).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
            if (await Game.Run(BankDeposits).ConfigureAwait(false) == !deposit)
                throw new ToolException($"The Gil Transfer window did not switch to {(deposit ? "deposit" : "withdraw")}.");
        }
        await Game.Run(() => FireBank(3, (uint)amount)).ConfigureAwait(false);
        await Task.Delay(400, ct).ConfigureAwait(false);
        await Game.Run(() => FireBank(0, null, close: true)).ConfigureAwait(false);
    }

    private static unsafe bool FireBank(int command, object? value, bool close = false)
    {
        var bank = Addon("Bank");
        if (bank == null) throw new ToolException("The Gil Transfer window closed unexpectedly.");
        RetainerUi.Fire(bank, false, command, value);
        if (close) bank->Close(true);
        return true;
    }

    /// <summary>Closes the Gil Transfer window without moving gil.</summary>
    public static Task CancelBank() => Game.Run(CloseBank);

    private static unsafe bool CloseBank()
    {
        if (Ready("Bank")) Addon("Bank")->Close(true);
        return true;
    }

    /// <summary>The InputNumeric "how many" window: enters a number and confirms.</summary>
    public static unsafe bool EnterNumber(int value)
    {
        if (!Ready("InputNumeric")) return false;
        RetainerUi.Fire(Addon("InputNumeric"), true, value);
        return true;
    }
}
