using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>Leaving the current duty like the Duty Finder's "Leave" entry: open the duty menu, pick Leave, confirm the prompt.</summary>
internal static class DutyExit
{
    // The Duty Finder menu shown inside a duty (AutoDuty's ExitDutyHelper uses the same agent).
    private const AgentId ContentsFinderMenu = (AgentId)233;

    public static bool InDuty() =>
        Svc.Condition[ConditionFlag.BoundByDuty] || Svc.Condition[ConditionFlag.BoundByDuty56] || Svc.Condition[ConditionFlag.BoundByDuty95];

    /// <summary>Leaves the duty, waiting for combat to end first; returns once the player is out of it (false if not in a duty).</summary>
    public static async Task<bool> Leave(TimeSpan timeout, CancellationToken ct, bool abortOnCombat = false)
    {
        var territory = await Game.Run(() => InDuty() ? Svc.ClientState.TerritoryType : 0).ConfigureAwait(false);
        if (territory == 0) return false;
        var until = DateTime.UtcNow + timeout;
        DateTime? pickedLeave = null;
        while (DateTime.UtcNow < until)
        {
            var step = await Game.Run(() =>
            {
                if (Svc.ClientState.TerritoryType != territory && !Svc.Condition[ConditionFlag.BetweenAreas]) return "left";
                if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return "loading";
                if (Svc.Condition[ConditionFlag.InCombat]) return "combat";
                unsafe
                {
                    var yesno = Addon("SelectYesno");
                    if (yesno != null && pickedLeave is { } t && DateTime.UtcNow - t < TimeSpan.FromSeconds(5))
                    {
                        RetainerUi.Fire(yesno, true, 0); // Yes
                        return "confirmed";
                    }
                    var menu = Addon("ContentsFinderMenu");
                    if (menu == null)
                    {
                        AgentModule.Instance()->GetAgentByInternalId(ContentsFinderMenu)->Show();
                        return "opening";
                    }
                    RetainerUi.Fire(menu, true, 0);   // Leave
                    RetainerUi.Fire(menu, false, -2);
                    pickedLeave = DateTime.UtcNow;
                    return "picked";
                }
            }).ConfigureAwait(false);
            if (step == "left") return true;
            if (step == "combat" && abortOnCombat) throw new ToolException("Combat started while leaving the duty.");
            await Task.Delay(step is "confirmed" or "loading" ? 2000 : 700, ct).ConfigureAwait(false);
        }
        throw new ToolException(await Game.Run(() => Svc.Condition[ConditionFlag.InCombat]).ConfigureAwait(false)
            ? "Could not leave the duty: still in combat."
            : "Could not leave the duty (the Duty Finder menu did not respond).");
    }

    private static unsafe AtkUnitBase* Addon(string name)
    {
        var a = Svc.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return a != null && a->IsVisible && a->IsReady ? a : null;
    }
}
