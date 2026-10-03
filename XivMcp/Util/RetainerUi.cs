using System;
using System.Collections.Generic;
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

    /// <summary>Windows shown while a retainer is being talked to (by the player or an automation plugin).</summary>
    private static readonly string[] RetainerWindows =
        ["SelectString", "Talk", "RetainerTaskAsk", "RetainerTaskResult", "RetainerTaskList", "RetainerSellList", "RetainerSell"];

    private static Dalamud.Game.NativeWrapper.AtkUnitBasePtr Ptr(string name) => Svc.GameGui.GetAddonByName(name, 1);
    private static unsafe AtkUnitBase* Addon(string name) => Svc.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
    public static bool Ready(string name) => Ptr(name) is { IsNull: false, IsVisible: true, IsReady: true };

    public static bool RetainerListOpen => Ready("RetainerList");

    public static bool InventoryOpen => Array.Exists(InventoryAddons, Ready);

    public static bool InventoryOpenFor(string retainer) => InventoryOpen && ActiveRetainerName == retainer;

    /// <summary>
    /// The retainer currently being talked to, or null. The game keeps reporting the last selected retainer as "active"
    /// even back at the retainer list, so this only counts while the list is not shown and a retainer window is open.
    /// </summary>
    public static unsafe string? ActiveRetainerName
    {
        get
        {
            if (RetainerListOpen) return null;
            if (!AtBell || (!InventoryOpen && !Array.Exists(RetainerWindows, Ready)))
                return null;
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
    public static Task Open(string name, CancellationToken ct) => Open(name, false, ct);

    /// <summary>Selects the named retainer and stops at its menu (instead of opening the inventory).</summary>
    public static Task OpenMenu(string name, CancellationToken ct) => Open(name, true, ct);

    private static async Task Open(string name, bool menuOnly, CancellationToken ct)
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
            if (await Svc.Framework.RunOnFrameworkThread(() => OpenStep(target.Index, target.Name, menuOnly, state)).ConfigureAwait(false)) return;
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
    private static unsafe bool OpenStep(int index, string name, bool menuOnly, StepState state)
    {
        var active = ActiveRetainerName;
        if (active == name && InventoryOpen && RetainerInventoryLoaded) return true;
        if (menuOnly && (active == name || (active is null && state.LastAction != DateTime.MinValue && !RetainerListOpen)) && Ready("SelectString") && !Ready("Talk")) return true;
        if (active is not null && active != name) throw new ToolException($"Retainer {active} opened instead of {name}; aborting.");
        if (state.Throttled) return false;

        // After selecting from the list, the retainer menu belongs to the retainer we selected.
        if ((active == name || (active is null && state.LastAction != DateTime.MinValue && !RetainerListOpen)) && Ready("SelectString"))
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
        // Done once no retainer window is left AND we are either back at the retainer list or no longer at the bell.
        // (After "Quit." the goodbye text plays and the list only reappears a moment later.)
        if (ActiveRetainerName is null && !InventoryOpen && !Ready("SelectString") && !Ready("Talk") &&
            (RetainerListOpen || !AtBell)) return true;
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

    /// <summary>Closes the retainer list (leaves the summoning bell). Call after <see cref="Close"/>.</summary>
    public static async Task CloseList(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var lastAction = DateTime.MinValue;
        // The bell session is over only when the game no longer considers the player at the bell; the list may still be
        // about to reappear, so keep watching until then.
        while (await Svc.Framework.RunOnFrameworkThread(() => RetainerListOpen || AtBell).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new ToolException("Timed out closing the retainer list; the game still reports the player at the bell. Close the window in game.");
            if (DateTime.UtcNow - lastAction > TimeSpan.FromSeconds(1) && await Svc.Framework.RunOnFrameworkThread(() => RetainerListOpen).ConfigureAwait(false))
            {
                await Svc.Framework.RunOnFrameworkThread(FireCloseList).ConfigureAwait(false);
                lastAction = DateTime.UtcNow;
            }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The game's "occupied at the summoning bell" state (also set at the company chest).</summary>
    public static bool AtBell => Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell];

    private static unsafe void FireCloseList() => Fire(Addon("RetainerList"), false, -1);

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

    internal static unsafe bool SelectMenuEntry(uint addonTextRow)
    {
        // The game text contains a placeholder ("Entrust or withdraw items. (Slots filled: )"); compare only the part before it.
        var full = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(addonTextRow).Text.ExtractText();
        var wanted = (full.IndexOf('(') is > 0 and var cut ? full[..cut] : full).Trim();
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

    /// <summary>Entries of the currently open SelectString menu, or null if none is open.</summary>
    public static unsafe List<string>? MenuEntries()
    {
        if (!Ready("SelectString")) return null;
        var menu = (AddonSelectString*)Addon("SelectString");
        var popup = &menu->PopupMenu.PopupMenu;
        var list = new List<string>();
        for (var i = 0; i < popup->EntryCount; i++)
            list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)popup->EntryNames[i].Value).TextValue.Trim());
        return list;
    }

    /// <summary>Selects an entry of the open SelectString menu by index.</summary>
    public static unsafe void SelectMenuIndex(int index)
    {
        var entries = MenuEntries() ?? throw new ToolException("No menu is open.");
        if (index < 0 || index >= entries.Count) throw new ToolException($"The menu has entries 0-{entries.Count - 1}.");
        Fire(Addon("SelectString"), true, index);
    }

    /// <summary>Advances a "Talk" dialogue box like a click does.</summary>
    public static unsafe void ClickTalk()
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

    internal static unsafe void Fire(AtkUnitBase* addon, bool updateState, params object?[] values)
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
