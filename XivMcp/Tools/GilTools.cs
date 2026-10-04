using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Mcp;
using XivMcp.Transfers;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Moving gil between the character and a retainer or the free company chest, through the game's own Gil Transfer window, without
/// any other plugin. FCCH, when installed, is waited for at the company chest.
/// </summary>
internal static class GilTools
{
    // Addon sheet rows: the retainer menu entry, the company chest's Gil tab, and the Deposit and Withdraw buttons.
    private const uint EntrustGilEntry = 2379;
    private static readonly uint[] GilTab = [830, 2883, 2991, 3202, 6871];
    private const uint WithdrawButton = 922, DepositButton = 923;

    public static IEnumerable<McpTool> Create(Configuration config, PluginCompat compat)
    {
        yield return new McpTool
        {
            Name = "move_gil",
            Description = "Moves gil between the character and a retainer or the free company chest, with the game's own Gil Transfer " +
                          "window. 'direction' is deposit (character → retainer or chest) or withdraw (→ character); 'amount' is a number " +
                          "(1200, 15k, 1.5m), a percentage (50%) or all. Retainer: at a summoning bell, with 'retainer' naming it (or the " +
                          "retainer that is open). Company chest: standing at the chest with its window open (navigate_to company_chest, " +
                          "then interact_with_object); FCCH, if installed, is waited for first. Checks the gil before and after. Requires " +
                          "'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "direction": { "type": "string", "enum": ["deposit", "withdraw"] },
                    "amount": { "type": "string", "description": "1200, 15k, 1.5m, 50% or all." },
                    "where": { "type": "string", "enum": ["retainer", "fc_chest"], "description": "Default: retainer." },
                    "retainer": { "type": "string", "description": "The retainer's name (default: the one that is open)." }
                  },
                  "required": ["direction", "amount"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (!config.AllowItemsRetainers)
                    throw new ToolException("Gil transfers are disabled. Enable \"Items & retainers\" in the XIV MCP settings window (/xivmcp) in game.");
                var deposit = args.String("direction")?.ToLowerInvariant() switch
                {
                    "deposit" => true, "withdraw" => false, _ => throw new ToolException("'direction' must be deposit or withdraw."),
                };
                var amountText = args.String("amount") ?? throw new ToolException("'amount' is required.");
                var chest = args.String("where")?.ToLowerInvariant() == "fc_chest";

                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return chest
                        ? await Chest(deposit, amountText, ct).ConfigureAwait(false)
                        : await Retainer(deposit, amountText, args.String("retainer"), compat, ct).ConfigureAwait(false);
                }
                finally { InventoryActionTools.Gate.Release(); }
            },
        };
    }

    private static async Task<object> Retainer(bool deposit, string amountText, string? name, PluginCompat compat, CancellationToken ct)
    {
        await Game.RunLoggedIn(() => { InventoryActionTools.EnsureNotBusy(); compat.AcquireBell(); return true; }).ConfigureAwait(false);
        var active = await Game.Run(() => RetainerUi.ActiveRetainerName).ConfigureAwait(false);
        name ??= active ?? throw new ToolException("No retainer is open. Name one with 'retainer' (at a summoning bell).");
        // The gil entry is in the retainer's menu: go back to it if its inventory is open, or open the retainer.
        if (active is not null && (!active.Equals(name, StringComparison.OrdinalIgnoreCase) || !await Game.Run(() => RetainerUi.Ready("SelectString")).ConfigureAwait(false)))
            await RetainerUi.Close(ct).ConfigureAwait(false);
        if (!await Game.Run(() => RetainerUi.Ready("SelectString") && RetainerUi.ActiveRetainerName is not null).ConfigureAwait(false))
            await RetainerUi.OpenMenu(name, ct).ConfigureAwait(false);
        var retainer = await Game.Run(() => RetainerUi.ActiveRetainerName).ConfigureAwait(false) ?? name;

        var (player, stored) = await Game.Run(Balances(chest: false)).ConfigureAwait(false);
        var amount = Amount(deposit, amountText, player, stored);
        if (!await Game.Run(() => RetainerUi.SelectMenuEntry(EntrustGilEntry)).ConfigureAwait(false))
            throw new ToolException($"{retainer}'s menu has no entry for entrusting gil.");
        await GameWindows.Bank(deposit, amount, ct).ConfigureAwait(false);
        return await Confirm(deposit, amount, player, stored, retainer, chest: false, ct).ConfigureAwait(false);
    }

    private static async Task<object> Chest(bool deposit, string amountText, CancellationToken ct)
    {
        await Game.RunLoggedIn(() =>
        {
            InventoryActionTools.EnsureNotBusy();
            if (!GameWindows.Ready("FreeCompanyChest"))
                throw new ToolException("The company chest is not open. Stand at it and open it (navigate_to company_chest, then interact_with_object).");
            return true;
        }).ConfigureAwait(false);
        await PluginCompat.WaitForFcch(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);

        // The Gil tab first: the chest only knows its gil once that tab was shown.
        await Game.Run(PressGilTab).ConfigureAwait(false);
        await Task.Delay(600, ct).ConfigureAwait(false);
        var (player, stored) = await Game.Run(Balances(chest: true)).ConfigureAwait(false);
        var amount = Amount(deposit, amountText, player, stored);

        if (!await Game.Run(() => PressChestButton(deposit)).ConfigureAwait(false))
            throw new ToolException($"The company chest's Gil tab has no {(deposit ? "Deposit" : "Withdraw")} button (your rank may not allow it).");
        if (await GameWindows.WaitFor(() => GameWindows.Ready("Bank") || GameWindows.Ready("InputNumeric"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false)
            && await Game.Run(() => GameWindows.Ready("InputNumeric")).ConfigureAwait(false))
            await Game.Run(() => GameWindows.EnterNumber((int)amount)).ConfigureAwait(false);
        else
            await GameWindows.Bank(deposit, amount, ct).ConfigureAwait(false);
        return await Confirm(deposit, amount, player, stored, "the company chest", chest: true, ct).ConfigureAwait(false);
    }

    private static unsafe bool PressGilTab() => GameWindows.Press(GameWindows.Addon("FreeCompanyChest"), GameWindows.Texts(GilTab));

    private static unsafe bool PressChestButton(bool deposit) =>
        GameWindows.Press(GameWindows.Addon("FreeCompanyChest"), GameWindows.Texts(deposit ? DepositButton : WithdrawButton));

    /// <summary>The character's gil, and the retainer's (open retainer) or the company chest's.</summary>
    private static Func<(long Player, long Stored)> Balances(bool chest) => () =>
    {
        unsafe
        {
            var im = InventoryManager.Instance();
            return (im->GetGil(), chest ? im->GetFreeCompanyGil() : im->GetRetainerGil());
        }
    };

    private static long Amount(bool deposit, string text, long player, long stored)
    {
        try
        {
            return deposit
                ? GilAmount.Parse(text, player, GilAmount.Cap - stored)
                : GilAmount.Parse(text, stored, GilAmount.Cap - player);
        }
        catch (FormatException ex) { throw new ToolException(ex.Message); }
    }

    /// <summary>Waits until the character's gil changed by the amount, and reports both balances.</summary>
    private static async Task<object> Confirm(bool deposit, long amount, long player, long stored, string where, bool chest, CancellationToken ct)
    {
        var expected = deposit ? player - amount : player + amount;
        var moved = await GameWindows.WaitFor(() => Balances(chest)().Player == expected, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        var (nowPlayer, nowStored) = await Game.Run(Balances(chest)).ConfigureAwait(false);
        if (!moved)
        {
            await GameWindows.CancelBank().ConfigureAwait(false);
            throw new ToolException($"The game did not move the gil (you have {nowPlayer:N0}, {where} {nowStored:N0}). Nothing may have changed; check the window.");
        }
        return new
        {
            moved = amount,
            direction = deposit ? "deposit" : "withdraw",
            where,
            yourGil = nowPlayer,
            storedGil = nowStored,
        };
    }
}
