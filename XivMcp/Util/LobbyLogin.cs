using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Memory;
using Framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Logging into a character through the game's own lobby, without other plugins: log out (if needed), at the title screen open the
/// data center selection, answer the service account choice (accounts with several), pick the data center and world, find the
/// character in the lobby list by content id and confirm — but only after the game's prompt names that character.
/// The window callbacks are the ones the lobby uses for mouse clicks (the same values login plugins use).
/// </summary>
internal static class LobbyLogin
{
    public sealed record Target(string Name, ulong ContentId, uint LobbyWorldId, string LobbyWorld, uint DataCenterId, int? ServiceAccount);

    /// <summary>The service account picked during the last login (so captured lists can be tagged with it).</summary>
    public static int? LastServiceAccount { get; private set; }

    private const uint LogoutCommand = 23;

    /// <summary>Logs into the target. With relog=true it logs out first even if the target is already in game (to show the lobby list).</summary>
    public static async Task<string> Run(Target target, TimeSpan timeout, Action<string> step, CancellationToken ct, bool relog = false)
    {
        var mustLeave = relog;
        var deadline = DateTime.UtcNow + timeout;
        var lastAction = DateTime.MinValue;
        var loggingOut = false;
        var worldSelected = false;
        var dcSelectedAt = DateTime.MinValue;
        var confirmed = false;
        var selectedAt = DateTime.MinValue;
        string? lastStep = null;
        void Step(string s) { if (s != lastStep) { step(s); lastStep = s; } }

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct).ConfigureAwait(false);
            var throttled = DateTime.UtcNow - lastAction < TimeSpan.FromMilliseconds(1200);
            var result = await Game.Run(() => Tick(target, throttled, ref mustLeave, ref loggingOut, ref worldSelected, ref dcSelectedAt, ref confirmed, ref selectedAt)).ConfigureAwait(false);
            switch (result.Kind)
            {
                case TickKind.Done: return result.Text!;
                case TickKind.Acted: lastAction = DateTime.UtcNow; step(result.Text!); lastStep = result.Text; break;
                case TickKind.Waiting: if (result.Text is not null) Step(result.Text); break;
            }
        }
        throw new ToolException($"{target.Name} is not in game after the timeout. Last step: {lastStep ?? "none"}. Check the game window.");
    }

    private enum TickKind { Waiting, Acted, Done }

    private readonly record struct TickResult(TickKind Kind, string? Text)
    {
        public static TickResult Wait(string? why = null) => new(TickKind.Waiting, why);
        public static TickResult Did(string what) => new(TickKind.Acted, what);
    }

    private static unsafe TickResult Tick(Target target, bool throttled, ref bool mustLeave, ref bool loggingOut, ref bool worldSelected, ref DateTime dcSelectedAt,
                                              ref bool confirmed, ref DateTime selectedAt)
    {
        // In game?
        if (Svc.ClientState.IsLoggedIn && Svc.Objects.LocalPlayer is { } player)
        {
            var me = Svc.PlayerState.ContentId;
            if (!loggingOut && !mustLeave && (me == target.ContentId || (target.ContentId == 0 && player.Name.TextValue.Equals(target.Name, StringComparison.OrdinalIgnoreCase))))
                return new TickResult(TickKind.Done, player.Name.TextValue);
            var logoutPrompt = loggingOut ? YesNo() : null;
            if (logoutPrompt != null)
            {
                if (throttled) return TickResult.Wait();
                Fire(logoutPrompt, 0);
                return TickResult.Did("Confirmed logging out.");
            }
            if (loggingOut) return TickResult.Wait("Logging out…");
            if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]) return TickResult.Wait();
            loggingOut = true;
            Framework.Instance()->GetUIModule()->ExecuteMainCommand(LogoutCommand);
            return TickResult.Did($"Logging out of {player.Name.TextValue}.");
        }
        loggingOut = false;
        mustLeave = false;

        // Lobby errors (server busy, maintenance, ...) are shown in a "Dialogue" box.
        var dialogue = Addon("Dialogue");
        if (dialogue != null && dialogue->IsVisible)
        {
            var parts = new List<string>();
            for (var i = 0; i < dialogue->UldManager.NodeListCount; i++)
            {
                var n = dialogue->UldManager.NodeList[i];
                if (n != null && n->Type == NodeType.Text && ((AtkTextNode*)n)->NodeText.ToString().Trim() is { Length: > 0 } t) parts.Add(t);
            }
            var text = string.Join(" ", parts);
            throw new ToolException($"The lobby shows an error: {text}");
        }

        // Confirmation for the character: only if the prompt names it.
        // Once the login is confirmed, only wait: the prompt can stay on screen through the login queue, and answering it again
        // restarts the login (back and forth between queue and loading screen).
        if (confirmed) return TickResult.Wait("Logging in (queue / loading)…");

        var yes = YesNo();
        if (yes != null)
        {
            var prompt = YesNoText(yes);
            if (!prompt.Contains(target.Name, StringComparison.OrdinalIgnoreCase))
                throw new ToolException($"The game asks \"{prompt}\", which is not about {target.Name}; not answering it.");
            if (throttled) return TickResult.Wait();
            Fire(yes, 0);
            confirmed = true;
            return TickResult.Did($"Logging in as {target.Name}.");
        }

        // Character list for the world.
        var list = Addon("_CharaSelectListMenu");
        var worldList = Addon("_CharaSelectWorldServer");
        var hasWorldList = worldList != null && worldList->IsVisible;
        if (list != null && list->IsVisible && (worldSelected || !hasWorldList))
        {
            // With a world list the character list shows one world; without it, every character of the data center.
            var index = ListIndex(target, perWorld: hasWorldList);
            if (index is null) return TickResult.Wait("Waiting for the character list…");
            if (index < 0) throw new ToolException($"{target.Name} is not in this account's character list on {target.LobbyWorld} " +
                                                   (target.ServiceAccount is { } sa ? $"(service account {sa + 1})." : "."));
            // Select once; only retry if no confirmation appeared for a while.
            if (throttled || DateTime.UtcNow - selectedAt < TimeSpan.FromSeconds(8)) return TickResult.Wait();
            Fire(list, 29, 0, index.Value);
            selectedAt = DateTime.UtcNow;
            return TickResult.Did($"Selected {target.Name}.");
        }

        // World list.
        var worlds = Addon("_CharaSelectWorldServer");
        if (worlds != null && worlds->IsVisible && !worldSelected)
        {
            var slot = WorldSlot(target.LobbyWorld);
            if (slot is null) return TickResult.Wait("Waiting for the world list…");
            if (slot < 0) throw new ToolException($"{target.LobbyWorld} is not in the world list — this account has no character there (on this service account).");
            if (throttled) return TickResult.Wait();
            Fire(worlds, 24, 0, slot.Value);
            worldSelected = true;
            return TickResult.Did($"Selected {target.LobbyWorld}.");
        }

        // Data center map.
        var dcMap = Addon("TitleDCWorldMap");
        if (dcMap != null && dcMap->IsVisible)
        {
            if (throttled) return TickResult.Wait();
            Fire(dcMap, 17, (int)target.DataCenterId);
            dcSelectedAt = DateTime.UtcNow;
            dcMap->Close(true);
            return TickResult.Did($"Selected data center {Svc.Data.GetExcelSheet<WorldDCGroupType>().GetRowOrDefault(target.DataCenterId)?.Name.ExtractText()}.");
        }

        // Service account choice.
        if (ServiceAccountMenu() is { } menu)
        {
            var choice = target.ServiceAccount ?? (menu.Entries == 1 ? 0 : -1);
            if (choice < 0)
                throw new ToolException($"This account has {menu.Entries} service accounts and XIV MCP doesn't know which one {target.Name} is on yet; " +
                                        "retry with service_account (1 = first).");
            if (choice >= menu.Entries) throw new ToolException($"There is no service account {choice + 1} (there are {menu.Entries}).");
            if (throttled) return TickResult.Wait();
            RetainerUi.SelectMenuIndex(choice);
            LastServiceAccount = choice;
            return TickResult.Did($"Selected service account {choice + 1}.");
        }

        // Title screen: open the data center selection (this also asks for the service account first, if there are several).
        var title = Addon("_TitleMenu");
        if (title != null && title->IsVisible && title->IsReady)
        {
            // After choosing the data center the title screen stays up while it connects: wait for the connection, don't start over.
            if (DateTime.UtcNow - dcSelectedAt < TimeSpan.FromSeconds(60)) return TickResult.Wait("Connecting to the data center…");
            if (dcSelectedAt != DateTime.MinValue)
                throw new ToolException("The data center connection did not open the character list within 60 s (server busy or a dialog is waiting).");
            worldSelected = false;
            if (throttled) return TickResult.Wait();
            Fire(title, 5);
            return TickResult.Did("Title screen: opening the data center selection.");
        }

        return TickResult.Wait();
    }

    /// <summary>Position of the target in the lobby list of its world: null while the list loads, -1 if it isn't there.</summary>
    private static unsafe int? ListIndex(Target target, bool perWorld)
    {
        var agent = AgentLobby.Instance();
        var entries = agent->LobbyData.CharaSelectEntries;
        if (entries.Count == 0) return null;
        var onWorld = new List<(ulong Id, string Name, byte Index)>();
        foreach (var ptr in entries)
        {
            var e = ptr.Value;
            if (e != null && e->ContentId != 0 && (!perWorld || e->CurrentWorldId == target.LobbyWorldId)) onWorld.Add((e->ContentId, e->NameString, e->Index));
        }
        var ordered = onWorld.OrderBy(e => e.Index).ToList();
        var i = ordered.FindIndex(e => (target.ContentId != 0 && e.Id == target.ContentId) || e.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        return i;
    }

    /// <summary>Position of a world in the lobby's world list (string array 1): null while it loads, -1 if missing.</summary>
    private static unsafe int? WorldSlot(string world)
    {
        var strings = Framework.Instance()->UIModule->GetRaptureAtkModule()->AtkModule.AtkArrayDataHolder.StringArrays[1];
        if (strings == null) return null;
        var any = false;
        for (var i = 0; i < 16; i++)
        {
            var p = strings->StringArray[i];
            if (p.Value == null) continue;
            var s = MemoryHelper.ReadStringNullTerminated((nint)p.Value).Trim();
            if (s.Length == 0) continue;
            any = true;
            if (s.Equals(world, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return any ? -1 : null;
    }

    private readonly record struct Menu(int Entries);

    /// <summary>The "Select a service account." menu (recognised by its prompt, Lobby sheet row 11), or null.</summary>
    private static unsafe Menu? ServiceAccountMenu()
    {
        var menu = (AddonSelectString*)Svc.GameGui.GetAddonByName<AtkUnitBase>("SelectString", 1);
        if (menu == null || !RetainerUi.Ready("SelectString") || menu->AtkUnitBase.UldManager.NodeListCount < 4) return null;
        var node = menu->AtkUnitBase.UldManager.NodeList[3];
        if (node == null || node->Type != NodeType.Text) return null;
        var prompt = Dalamud.Game.Text.SeStringHandling.SeString.Parse(((AtkTextNode*)node)->NodeText).TextValue.Trim();
        var expected = Svc.Data.GetExcelSheet<Lobby>().GetRowOrDefault(11)?.Text.ExtractText().Trim();
        return expected is not null && prompt == expected ? new Menu(menu->PopupMenu.PopupMenu.EntryCount) : null;
    }

    private static unsafe AtkUnitBase* YesNo()
    {
        var a = Addon("SelectYesno");
        return a != null && a->IsVisible && a->IsReady ? a : null;
    }

    private static unsafe string YesNoText(AtkUnitBase* addon)
    {
        var yesno = (AddonSelectYesno*)addon;
        return yesno->PromptText == null ? "" : Dalamud.Game.Text.SeStringHandling.SeString.Parse(yesno->PromptText->NodeText).TextValue;
    }

    private static unsafe AtkUnitBase* Addon(string name) => Svc.GameGui.GetAddonByName<AtkUnitBase>(name, 1);

    private static unsafe void Fire(AtkUnitBase* addon, params int[] values)
    {
        var atk = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atk[i] = default;
            atk[i].Type = AtkValueType.Int;
            atk[i].Int = values[i];
        }
        addon->FireCallback((uint)values.Length, atk);
    }
}
