using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Shops;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Buying Ceruleum Tanks with company credits from the mammet in the FC workshop (Mammet Voyager #004A, "Company Credit Exchange",
/// FccShop 2752515). Its window (FreeCompanyCreditShop) takes the quantity in the row's number field and buys on a click on the row,
/// then asks to confirm — the steps AutoRetainer's fuel purchase takes in the same window.
/// </summary>
internal static class CreditShopTools
{
    private const uint Mammet = 1011274, CreditShopRow = 2752515, Ceruleum = 10155;
    private const string Window = "FreeCompanyCreditShop";
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        yield return new McpTool
        {
            Name = "buy_ceruleum_tanks",
            Description = "Buys Ceruleum Tanks, the submersibles' fuel, with the free company's credits from the mammet in the FC workshop " +
                          "(Mammet Voyager #004A, Company Credit Exchange): walks to it in the workshop, opens the exchange and buys in " +
                          "purchases of up to 99, as far as the credits go. Company credits are not gil, so it asks in game first; the player " +
                          "approves there. Checks that the character may spend company credits and that the bags have room. Be in the workshop " +
                          "(navigate_to destination workshop). Requires 'Market & purchases' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "quantity": { "type": "integer", "minimum": 1, "maximum": 9999, "description": "How many tanks to buy." }
                  },
                  "required": ["quantity"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (!config.AllowMarketPurchases)
                    throw new ToolException("Buying is disabled. Enable \"Market & purchases\" in the XIV MCP settings window (/xivmcp) in game.");
                var quantity = args.Int("quantity", 0, 0, 9999);
                if (quantity <= 0) throw new ToolException("'quantity' must be at least 1.");
                if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new ToolException("Another tank purchase is running.");
                var steps = new List<string>();
                try
                {
                    var before = await Game.RunLoggedIn(() =>
                    {
                        InventoryActionTools.EnsureNotBusy();
                        var slots = Items.SlotsNeeded(Ceruleum, quantity);
                        if (slots > Items.FreeBagSlots()) throw new ToolException($"{quantity} Ceruleum Tanks need {slots} free bag slots; only {Items.FreeBagSlots()} are free.");
                        return Items.CountInBags(Ceruleum);
                    }).ConfigureAwait(false);

                    await GoToMammet(config, steps, ct).ConfigureAwait(false);
                    await OpenExchange(steps, ct).ConfigureAwait(false);
                    try
                    {
                        var shop = await Game.Run(ReadShop).ConfigureAwait(false);
                        if (!shop.MaySpend) throw new ToolException("This character is not allowed to spend company credits (the free company's rank permissions).");
                        var row = shop.Find(Ceruleum) ?? throw new ToolException("The exchange doesn't list Ceruleum Tanks.");
                        if (row.Index != 0) throw new ToolException("Ceruleum Tanks are not the exchange's first row; buying from other rows is not supported.");
                        var batches = CreditShop.Batches(quantity, shop.Credits, row.Price, row.PerPurchase);
                        if (batches.Count == 0) throw new ToolException($"The company has {shop.Credits:N0} credits; a tank costs {row.Price:N0}.");
                        var total = batches.Sum();

                        steps.Add("Waiting for approval in game.");
                        await Consent.Require("Buy Ceruleum Tanks with company credits?",
                        [
                            $"Buy {total} Ceruleum Tanks from Mammet Voyager #004A" + (total < quantity ? $" (asked for {quantity}; the credits pay for {total})" : ""),
                            $"{(long)total * row.Price:N0} of the company's {shop.Credits:N0} credits ({row.Price:N0} each)",
                        ], ConsentTimeout, ct).ConfigureAwait(false);
                        steps.Add("Approved in game.");

                        foreach (var batch in batches) await BuyBatch(batch, steps, ct).ConfigureAwait(false);
                        var credits = (await Game.Run(ReadShop).ConfigureAwait(false)).Credits;
                        var now = await Game.Run(() => Items.CountInBags(Ceruleum)).ConfigureAwait(false);
                        return new { bought = now - before, requested = quantity, creditsSpent = shop.Credits - credits, creditsLeft = credits, nowInBags = now, steps };
                    }
                    finally
                    {
                        await CloseExchange(ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Gate.Release();
                }
            },
        };
    }

    /// <summary>Walks to the mammet in the workshop, unless it is already at hand.</summary>
    private static async Task GoToMammet(Configuration config, List<string> steps, CancellationToken ct)
    {
        if (await Game.Run(() => Svc.Objects.Any(o => o.BaseId == Mammet && o.IsTargetable && Game.DistanceToPlayer(o.Position) < 4)).ConfigureAwait(false)) return;
        if (!config.AllowGameNavigation) throw new ToolException("The mammet is not at hand and 'Game & navigation' is off; walk up to Mammet Voyager #004A in the workshop first.");
        if (await NavigationTools.WalkTo("Mammet Voyager #004A", o => o.BaseId == Mammet, steps, ct).ConfigureAwait(false) is null)
            throw new ToolException("Mammet Voyager #004A is not here: go to the free company workshop first (navigate_to destination workshop).");
    }

    /// <summary>Talks to the mammet and picks "Company Credit Exchange" from its menu.</summary>
    private static async Task OpenExchange(List<string> steps, CancellationToken ct)
    {
        if (await Game.Run(() => GameWindows.Ready(Window)).ConfigureAwait(false)) return;
        var shopName = await Game.Run(() => Svc.Data.GetExcelSheet<FccShop>().GetRowOrDefault(CreditShopRow)?.Name.ExtractText() ?? "Company Credit Exchange").ConfigureAwait(false);
        await Game.RunLoggedIn(() =>
        {
            var npc = Svc.Objects.Where(o => o.BaseId == Mammet && o.IsTargetable).OrderBy(o => Game.DistanceToPlayer(o.Position)).FirstOrDefault()
                      ?? throw new ToolException("Mammet Voyager #004A is not nearby.");
            unsafe
            {
                var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address;
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->SetHardTarget(native, false, false, 0);
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(native, true);
            }
            return true;
        }).ConfigureAwait(false);
        steps.Add("Talking to Mammet Voyager #004A.");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        var picked = false;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, ct).ConfigureAwait(false);
            var state = await Game.Run(() =>
            {
                if (GameWindows.Ready(Window)) return "open";
                if (picked || ShopTools.MenuEntries(out var menu) is not { } entries) return null;
                var index = entries.FindIndex(e => e.Contains(shopName, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return "menu:" + string.Join(" | ", entries);
                ShopTools.FireMenu(menu, index);
                return "picked";
            }).ConfigureAwait(false);
            if (state == "open") { steps.Add("Company Credit Exchange open."); await Task.Delay(400, ct).ConfigureAwait(false); return; }
            if (state?.StartsWith("menu:") == true) throw new ToolException($"The mammet's menu has no \"{shopName}\" entry: {state[5..]}.");
            if (state == "picked") picked = true;
        }
        throw new ToolException("The Company Credit Exchange did not open.");
    }

    private static unsafe CreditShop ReadShop()
    {
        var addon = GameWindows.Addon(Window);
        if (addon == null || !addon->IsVisible) throw new ToolException("The Company Credit Exchange closed.");
        AtkValue? At(int i) => i < addon->AtkValuesCount ? addon->AtkValues[i] : null;
        return CreditShop.Read(
            i => At(i) is { } v ? v.Type switch { AtkValueType.UInt => v.UInt, AtkValueType.Int => (uint)Math.Max(0, v.Int), _ => 0 } : 0,
            i => At(i) is { Type: AtkValueType.Bool } v && v.Byte != 0,
            i => At(i) is { } v && v.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8 && v.String.Value != null
                ? MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue : null);
    }

    /// <summary>One purchase: the quantity into the row's number field, a click on the row, Yes to the game's question.</summary>
    private static async Task BuyBatch(int quantity, List<string> steps, CancellationToken ct)
    {
        var have = await Game.Run(() => Items.CountInBags(Ceruleum)).ConfigureAwait(false);
        await Game.Run(() => { ClickRow(quantity); return true; }).ConfigureAwait(false);
        var names = await Game.Run(() => Svc.Data.GetExcelSheet<Item>().GetRow(Ceruleum) is var item
            ? new[] { item.Name.ExtractText(), item.Singular.ExtractText(), item.Plural.ExtractText() }.Where(n => n.Length > 0).ToArray() : []).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            var arrived = await Game.Run(() =>
            {
                ConfirmPurchase(names);
                return Items.CountInBags(Ceruleum) > have;
            }).ConfigureAwait(false);
            if (arrived)
            {
                steps.Add($"Bought {quantity}.");
                await Task.Delay(Random.Shared.Next(500, 800), ct).ConfigureAwait(false);
                return;
            }
        }
        throw new ToolException($"The purchase of {quantity} didn't go through; the exchange may have refused it.");
    }

    /// <summary>Sets the first row's number field and clicks the row (event 35), as the mouse does.</summary>
    private static unsafe void ClickRow(int quantity)
    {
        var addon = GameWindows.Addon(Window);
        if (addon == null || !addon->IsVisible) throw new ToolException("The Company Credit Exchange closed.");
        var list = addon->GetComponentNodeById(27);
        var row = list == null || list->Component == null ? null : list->Component->GetNodeById(5);
        var renderer = row == null ? null : row->GetAsAtkComponentListItemRenderer();
        var field = renderer == null ? null : renderer->GetNodeById(5);
        var input = field == null ? null : field->GetAsAtkComponentNumericInput();
        if (input == null) throw new ToolException("The exchange's quantity field was not found.");
        input->SetValue(quantity);
        var evt = new AtkEvent { Listener = (AtkEventListener*)addon, Target = &AtkStage.Instance()->AtkEventTarget };
        var data = new AtkEventData();
        addon->ReceiveEvent((AtkEventType)35, 0, &evt, &data);
    }

    /// <summary>Answers Yes to the game's question, but only when it is about the tanks.</summary>
    private static unsafe void ConfirmPurchase(string[] names)
    {
        var yesno = (AddonSelectYesno*)GameWindows.Addon("SelectYesno");
        if (yesno == null || !yesno->AtkUnitBase.IsVisible || yesno->PromptText == null || yesno->YesButton == null || !yesno->YesButton->IsEnabled) return;
        var prompt = Dalamud.Game.Text.SeStringHandling.SeString.Parse(yesno->PromptText->NodeText).TextValue;
        if (!names.Any(n => prompt.Contains(n, StringComparison.OrdinalIgnoreCase)))
            throw new ToolException($"The game asks \"{prompt}\", which is not about Ceruleum Tanks; not answering it.");
        RetainerUi.Fire(&yesno->AtkUnitBase, true, 0);
    }

    /// <summary>Closes the exchange and leaves the mammet's menu, if it comes back.</summary>
    private static async Task CloseExchange(CancellationToken ct)
    {
        for (var i = 0; i < 10 && await Game.Run(() => GameWindows.Ready(Window)).ConfigureAwait(false); i++)
        {
            await Game.Run(() => { unsafe { RetainerUi.Fire(GameWindows.Addon(Window), true, -1); } return true; }).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        await Task.Delay(300, ct).ConfigureAwait(false);
        await Game.Run(() =>
        {
            if (ShopTools.MenuEntries(out var menu) is { } entries && menu != 0)
            {
                var quit = entries.FindIndex(e => e.StartsWith("Nothing", StringComparison.OrdinalIgnoreCase) || e.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase));
                if (quit >= 0) ShopTools.FireMenu(menu, quit);
                else unsafe { ((AtkUnitBase*)menu)->Close(true); }
            }
            return true;
        }).ConfigureAwait(false);
    }
}
