using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Drives the summoning bell windows (retainer list → greeting → menu → inventory, and back), the same way
/// AutoRetainer does: AtkUnitBase callbacks for the list and menu, a synthetic click to advance the greeting.
/// </summary>
internal static class RetainerUi
{
    private const uint AddonEntrustOrWithdraw = 2378; // "Entrust or withdraw items."
    private const uint AddonQuit = 2383;              // "Quit."

    private static readonly string[] InventoryAddons = ["InventoryRetainerLarge", "InventoryRetainer"];

    private static Dalamud.Game.NativeWrapper.AtkUnitBasePtr Ptr(string name) => Svc.GameGui.GetAddonByName(name, 1);
    private static unsafe AtkUnitBase* Addon(string name) => Svc.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
    private static bool Ready(string name) => Ptr(name) is { IsNull: false, IsVisible: true, IsReady: true };

    public static bool RetainerListOpen => Ready("RetainerList");

    public static bool InventoryOpen => Array.Exists(InventoryAddons, Ready);

    public static bool InventoryOpenFor(string retainer) => InventoryOpen && ActiveRetainerName == retainer;

    public static unsafe string? ActiveRetainerName
    {
        get
        {
            var rm = RetainerManager.Instance();
            var r = rm == null ? null : rm->GetActiveRetainer();
            return r == null ? null : r->NameString;
        }
    }

    public static unsafe bool RetainerInventoryLoaded
    {
        get
        {
            var c = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerPage1);
            return c != null && c->IsLoaded;
        }
    }

    /// <summary>Opens the inventory of the named retainer. The retainer list (summoning bell) or another retainer must be open.</summary>
    public static async Task Open(string name, CancellationToken ct)
    {
        var target = await Svc.Framework.RunOnFrameworkThread(() => ResolveSortedIndex(name)).ConfigureAwait(false);
        var current = await Svc.Framework.RunOnFrameworkThread(() => ActiveRetainerName).ConfigureAwait(false);
        if (current is not null && !current.Equals(target.Name, StringComparison.Ordinal))
            await Close(ct).ConfigureAwait(false);

        var state = new StepState();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) throw new ToolException($"Timed out opening retainer {target.Name}. Current windows may need manual attention.");
            if (await Svc.Framework.RunOnFrameworkThread(() => OpenStep(target.Index, target.Name, state)).ConfigureAwait(false)) return;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the current retainer and returns to the retainer list. No-op if no retainer is open.</summary>
    public static async Task Close(CancellationToken ct)
    {
        var state = new StepState();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) throw new ToolException("Timed out closing the retainer. Current windows may need manual attention.");
            if (await Svc.Framework.RunOnFrameworkThread(() => CloseStep(state)).ConfigureAwait(false)) return;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    private sealed class StepState
    {
        public DateTime LastAction = DateTime.MinValue;
        public bool Throttled => DateTime.UtcNow - LastAction < TimeSpan.FromMilliseconds(800);
        public void Acted() => LastAction = DateTime.UtcNow;
    }

    /// <summary>One framework tick of opening a retainer. Returns true once its inventory is open and loaded.</summary>
    private static unsafe bool OpenStep(int index, string name, StepState state)
    {
        var active = ActiveRetainerName;
        if (active == name && InventoryOpen && RetainerInventoryLoaded) return true;
        if (active is not null && active != name) throw new ToolException($"Retainer {active} opened instead of {name}; aborting.");
        if (state.Throttled) return false;

        if (active == name && Ready("SelectString"))
        {
            if (!SelectMenuEntry(AddonEntrustOrWithdraw)) throw new ToolException("Could not find \"Entrust or withdraw items\" in the retainer menu.");
            state.Acted();
        }
        else if (Ready("Talk"))
        {
            ClickTalk();
            state.Acted();
        }
        else if (active is null && RetainerListOpen && DateTime.UtcNow - state.LastAction > TimeSpan.FromSeconds(3))
        {
            Fire(Addon("RetainerList"), true, 2, (uint)index, null, null);
            state.Acted();
        }
        else if (active is null && !RetainerListOpen && state.LastAction == DateTime.MinValue)
        {
            throw new ToolException("The retainer list is not open. Use a summoning bell first.");
        }
        return false;
    }

    /// <summary>One framework tick of closing a retainer. Returns true once back at the retainer list (or nothing is open).</summary>
    private static unsafe bool CloseStep(StepState state)
    {
        if (ActiveRetainerName is null && !InventoryOpen && !Ready("SelectString") && !Ready("Talk")) return true;
        if (state.Throttled) return false;

        if (InventoryOpen)
        {
            foreach (var addon in InventoryAddons)
                if (Ready(addon)) Addon(addon)->Close(true);
            state.Acted();
        }
        else if (Ready("SelectString"))
        {
            if (!SelectMenuEntry(AddonQuit)) throw new ToolException("Could not find \"Quit\" in the retainer menu.");
            state.Acted();
        }
        else if (Ready("Talk"))
        {
            ClickTalk();
            state.Acted();
        }
        return false;
    }

    /// <summary>Position of a retainer in the retainer list (the list uses the player's display order).</summary>
    private static unsafe (int Index, string Name) ResolveSortedIndex(string name)
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) throw new ToolException("Retainer data is not loaded. Open the retainer list at a summoning bell.");
        string? fuzzy = null;
        var fuzzyIndex = -1;
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0) continue;
            var n = r->NameString;
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return ((int)i, n);
            if (n.StartsWith(name, StringComparison.OrdinalIgnoreCase)) { fuzzy = fuzzy is null ? n : ""; fuzzyIndex = (int)i; }
        }
        if (!string.IsNullOrEmpty(fuzzy)) return (fuzzyIndex, fuzzy);
        throw new ToolException($"No retainer named '{name}'.");
    }

    private static unsafe bool SelectMenuEntry(uint addonTextRow)
    {
        var wanted = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(addonTextRow).Text.ExtractText().Trim();
        var menu = (AddonSelectString*)Addon("SelectString");
        if (menu == null) return false;
        var popup = &menu->PopupMenu.PopupMenu;
        for (var i = 0; i < popup->EntryCount; i++)
        {
            var text = MemoryHelper.ReadSeStringNullTerminated((nint)popup->EntryNames[i].Value).TextValue.Trim();
            if (text.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) || wanted.StartsWith(text, StringComparison.OrdinalIgnoreCase) && text.Length > 3)
            {
                Fire((AtkUnitBase*)menu, true, i);
                return true;
            }
        }
        return false;
    }

    /// <summary>Advances a "Talk" dialogue box like a click does.</summary>
    private static unsafe void ClickTalk()
    {
        var talk = Addon("Talk");
        if (talk == null) return;
        var evt = new AtkEvent
        {
            Listener = (AtkEventListener*)talk,
            Target = &AtkStage.Instance()->AtkEventTarget,
        };
        evt.State.StateFlags = (AtkEventStateFlags)132;
        var data = new AtkEventData();
        var listener = (AtkEventListener*)talk;
        listener->ReceiveEvent((AtkEventType)3, 0, &evt, &data);
        listener->ReceiveEvent((AtkEventType)9, 0, &evt, &data);
        listener->ReceiveEvent((AtkEventType)4, 0, &evt, &data);
    }

    private static unsafe void Fire(AtkUnitBase* addon, bool updateState, params object?[] values)
    {
        if (addon == null) throw new ToolException("Window disappeared.");
        var atk = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atk[i] = default;
            switch (values[i])
            {
                case int v: atk[i].Type = AtkValueType.Int; atk[i].Int = v; break;
                case uint v: atk[i].Type = AtkValueType.UInt; atk[i].UInt = v; break;
                case null: atk[i].Type = AtkValueType.Undefined; break;
                default: throw new ArgumentException($"Unsupported callback value {values[i]}");
            }
        }
        addon->FireCallback((uint)values.Length, atk, updateState);
    }
}
