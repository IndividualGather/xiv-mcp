using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.CustomDeliveries;
using XivMcp.Jobs;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Custom deliveries with Satisfier: this week's requests, delivering one client's request by crafting (Artisan), gathering
/// (Questionable) or fishing (XIV MCP's own AutoHook fishing, as Satisfier only travels to the spot), and a job for the whole week.
/// </summary>
internal static class CustomDeliveryTools
{
    public static IEnumerable<McpTool> Create(Configuration config, Func<JobManager> jobs, Func<string?> client)
    {
        yield return new McpTool
        {
            Name = "get_custom_deliveries",
            Description = "This week's custom deliveries, from Satisfier: the deliveries left this week (for all clients together), and per " +
                          "client whether you unlocked them, their rank and satisfaction, the deliveries left, and their three requests (to " +
                          "craft, to gather, to fish) with the item, whether it carries the bonus, how many it still takes and how many you " +
                          "carry. Also which kinds can be delivered automatically: crafting needs Artisan, gathering Questionable, fishing AutoHook.",
            InputSchema = """{ "type": "object", "properties": {} }""",
            ReadOnly = true,
            Handler = (_, ct) => Game.Run(Overview),
        };

        yield return new McpTool
        {
            Name = "deliver_custom_delivery",
            Description = "Delivers one client's request with Satisfier and waits until it is done. 'kind' craft: buys the ingredient from " +
                          "the client's vendor, crafts with Artisan and turns in; gather: gathers with Questionable and turns in; fish: turns " +
                          "in the fish you carry (catch them first: do_custom_deliveries does both, or catch_fish). Without 'kind': gathering " +
                          "(Miner or Botanist), else crafting; fishing only when the player asks for it. Requires 'Custom Deliveries' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "client": { "type": "string", "description": "The client's name (as get_custom_deliveries names them)." },
                    "kind": { "type": "string", "enum": ["craft", "gather", "fish"], "description": "Default gather, else craft. Use 'fish' only when the player asked for fishing." }
                  },
                  "required": ["client"]
                }
                """,
            ReadOnly = false,
            Handler = (args, ct) => Deliver(args.String("client") ?? throw new ToolException("'client' is required."), Kind(args), ct),
        };

        yield return new McpTool
        {
            Name = "do_custom_deliveries",
            Description = "Starts a background job for this week's custom deliveries: plans every client's remaining deliveries within the " +
                          "weekly allowance (gathering with Miner or Botanist, else crafting; never fishing unless asked for; only kinds a plugin can do), then " +
                          "delivers them one client after another with deliver_custom_delivery. Fishing requests are caught first with " +
                          "AutoHook (switch to Fisher, bait, the spot, fish_until), only with kind 'fish'. Optional 'kind' for everyone and 'clients' to limit it. " +
                          "Follow it with get_job. Requires 'Custom Deliveries' in /xivmcp; each step follows its own module.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "kind": { "type": "string", "enum": ["craft", "gather", "fish"], "description": "Deliver this kind for every client. Use 'fish' only when the player asked for fishing." },
                    "clients": { "type": "array", "items": { "type": "string" }, "description": "Only these clients." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var only = args.Array("clients")?.Select(n => n?.ToString() ?? "").Where(n => n.Length > 0).ToList();
                var (clients, allowances) = await Game.Run(() => (SatisfierBridge.Clients(), SatisfierBridge.Allowances())).ConfigureAwait(false);
                var plan = DeliveryPlan.Choose(clients.Select(c => c.ForPlan), Possible(), allowances, Kind(args), only);
                if (plan.Count == 0)
                    throw new ToolException(allowances == 0 ? "No custom deliveries left this week." : "Nothing to deliver: no unlocked client has a request a plugin can do.");

                var steps = new List<JobManager.Step>();
                foreach (var (run, i) in plan.Select((r, i) => (r, i)))
                {
                    var c = clients.First(x => x.Name == run.Npc.Name);
                    if (run.Kind == DeliveryKind.Fish)
                    {
                        if (c.SpearFish) throw new ToolException($"{c.Name} wants a spearfished catch, which XIV MCP can't do yet. Leave out {c.Name}, or choose another kind.");
                        var have = await Game.Run(() => Items.CountInBags(c.Items[2])).ConfigureAwait(false);
                        if (have < run.Count)
                        {
                            var fishArgs = new ToolArgs(new JsonObject { ["fish"] = c.Items[2].ToString(), ["spot"] = c.FishSpot.ToString() });
                            var (_, catchSteps, _) = await AutoHookTools.CatchSteps(fishArgs, config, run.Count - have, 99, 240, $"c{i}-", ct).ConfigureAwait(false);
                            steps.AddRange(catchSteps);
                        }
                    }
                    steps.Add(new()
                    {
                        Id = $"d{i}", Tool = "deliver_custom_delivery",
                        Args = new JsonObject { ["client"] = c.Name, ["kind"] = run.Kind.ToString().ToLowerInvariant() },
                        Note = $"Deliver {run.Count} {Items.Name(c.Items[(int)run.Kind])} to {c.Name} ({run.Kind.ToString().ToLowerInvariant()})",
                    });
                }
                var job = jobs().Start($"Custom deliveries: {plan.Sum(p => p.Count)} for {plan.Count} client{(plan.Count == 1 ? "" : "s")}", steps, client());
                return new
                {
                    started = JobManager.Describe(job),
                    allowancesLeft = allowances,
                    plan = plan.Select(p => new { client = p.Npc.Name, kind = p.Kind.ToString().ToLowerInvariant(), count = p.Count, bonus = p.Npc.Bonus.Contains(p.Kind) }),
                };
            },
        };

        yield return new McpTool
        {
            Name = "stop_custom_delivery",
            Description = "Stops the custom delivery Satisfier is doing. Requires 'Custom Deliveries' in /xivmcp.",
            InputSchema = """{ "type": "object", "properties": {} }""",
            ReadOnly = false,
            Handler = (_, ct) => Game.Run(() =>
            {
                var was = SatisfierBridge.Running ? SatisfierBridge.Status : null;
                SatisfierBridge.Stop();
                return (object)new { stopped = was is not null, was };
            }),
        };
    }

    private static DeliveryKind? Kind(ToolArgs args) => args.String("kind")?.ToLowerInvariant() switch
    {
        null => null,
        "craft" => DeliveryKind.Craft,
        "gather" => DeliveryKind.Gather,
        "fish" => DeliveryKind.Fish,
        var k => throw new ToolException($"'kind' must be craft, gather or fish, not '{k}'."),
    };

    /// <summary>The kinds a plugin can do now: crafting with Artisan, gathering with Questionable, fishing with AutoHook.</summary>
    private static HashSet<DeliveryKind> Possible()
    {
        var kinds = new HashSet<DeliveryKind>();
        if (PluginCompat.IsLoaded("Artisan")) kinds.Add(DeliveryKind.Craft);
        if (PluginCompat.IsLoaded("Questionable")) kinds.Add(DeliveryKind.Gather);
        if (AutoHookTools.Loaded) kinds.Add(DeliveryKind.Fish);
        return kinds;
    }

    private static readonly string[] KindNames = ["craft", "gather", "fish"];

    private static object Overview()
    {
        var possible = Possible();
        return new
        {
            deliveriesLeftThisWeek = SatisfierBridge.Allowances(),
            automatic = KindNames.Where((_, i) => possible.Contains((DeliveryKind)i)),
            clients = SatisfierBridge.Clients().Select(c => new
            {
                client = c.Name,
                unlocked = c.Unlocked,
                rank = c.Rank,
                satisfaction = c.SatisfactionMax > 0 ? $"{c.Satisfaction}/{c.SatisfactionMax}" : null,
                deliveriesLeft = c.DeliveriesLeft,
                requests = c.Unlocked && c.Rank > 0
                    ? Enumerable.Range(0, 3).Select(i => new
                    {
                        kind = KindNames[i],
                        item = Items.Name(c.Items[i]),
                        bonus = c.Bonus[i],
                        stillTakes = c.Remaining[i],
                        inBags = Items.CountInBags(c.Items[i]),
                        note = i == 2 && c.SpearFish ? "spearfishing" : null,
                    })
                    : null,
            }),
        };
    }

    private static async Task<object> Deliver(string name, DeliveryKind? kind, CancellationToken ct)
    {
        var c = await Game.Run(() => Find(name)).ConfigureAwait(false);
        if (!c.Unlocked) throw new ToolException($"{c.Name} is not unlocked yet.");
        if (c.DeliveriesLeft == 0) throw new ToolException($"{c.Name} takes no more deliveries this week.");
        if (await Game.Run(SatisfierBridge.Allowances).ConfigureAwait(false) == 0) throw new ToolException("No custom deliveries left this week.");
        var chosen = kind ?? DeliveryPlan.Choose([c.ForPlan], Possible(), 99).FirstOrDefault()?.Kind
                     ?? throw new ToolException($"None of {c.Name}'s requests can be delivered automatically now (crafting needs Artisan, gathering Questionable).");
        switch (chosen)
        {
            case DeliveryKind.Craft when !PluginCompat.IsLoaded("Artisan"):
                throw new ToolException("Crafting deliveries need Artisan, which isn't loaded.");
            case DeliveryKind.Gather when !PluginCompat.IsLoaded("Questionable"):
                throw new ToolException("Gathering deliveries need Questionable, which isn't loaded.");
            case DeliveryKind.Fish:
                var have = await Game.Run(() => Items.CountInBags(c.Items[2])).ConfigureAwait(false);
                if (have == 0)
                    throw new ToolException($"Catch {Items.Name(c.Items[2])} first (catch_fish), or let do_custom_deliveries catch and deliver them.");
                break;
        }

        var item = Items.Name(c.Items[(int)chosen]);
        var planned = c.Remaining[(int)chosen];
        var statuses = new List<string>();
        var rankedUp = false;
        var window = await Game.Run(SatisfierBridge.KeepWindowOpen).ConfigureAwait(false);
        try
        {
            await Game.Run(() => { SatisfierBridge.Start(c, chosen); return true; }).ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var deadline = DateTime.UtcNow.AddHours(2);
            while (await Game.Run(() => SatisfierBridge.Running).ConfigureAwait(false))
            {
                if (DateTime.UtcNow > deadline) throw new ToolException($"Satisfier is still busy after two hours ({statuses.LastOrDefault()}).");
                // A rank-up shows its rewards in a window that waits for Accept; Satisfier doesn't press it.
                if (await Game.Run(AcceptRankUp).ConfigureAwait(false)) statuses.Add("Accepted the rank-up rewards.");
                var status = await Game.Run(() => SatisfierBridge.Status).ConfigureAwait(false);
                if (status.Length > 0 && statuses.LastOrDefault() != status)
                {
                    statuses.Add(status);
                    JobManager.Instance?.StepProgress("deliver_custom_delivery", status);
                }
                // Done once the planned deliveries are in. A rank-up brings new requests, which Satisfier would wait for in the
                // turn-in window without the items: stop it there too.
                var now = await Game.Run(() => Find(c.Name)).ConfigureAwait(false);
                rankedUp = now.Rank != c.Rank;
                if (c.DeliveriesLeft - now.DeliveriesLeft >= planned || rankedUp)
                {
                    await Game.Run(() =>
                    {
                        SatisfierBridge.Stop();
                        if (RetainerUi.Ready("SatisfactionSupply")) CloseWindow("SatisfactionSupply");
                        return true;
                    }).ConfigureAwait(false);
                    if (rankedUp) statuses.Add($"{c.Name} ranked up: new requests.");
                    break;
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await Game.Run(() => { SatisfierBridge.Stop(); return true; }).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await Game.Run(() => { window.Dispose(); return true; }).ConfigureAwait(false);
        }

        // What went in: the client's deliveries left this week (Satisfier's per-request count starts over after a rank-up).
        var after = await Game.Run(() => Find(c.Name)).ConfigureAwait(false);
        var delivered = c.DeliveriesLeft - after.DeliveriesLeft;
        if (delivered <= 0)
            throw new ToolException($"Satisfier stopped without delivering {item} to {c.Name}. Its last step: {statuses.LastOrDefault() ?? "none"}. " +
                                    "Satisfier's window (/vsatisfy) and the Dalamud log say more.");
        return new
        {
            client = c.Name,
            kind = chosen.ToString().ToLowerInvariant(),
            item,
            delivered,
            rank = after.Rank,
            rankedUp = rankedUp ? $"{c.Name} reached rank {after.Rank}: new requests. Plan the rest again (get_custom_deliveries, do_custom_deliveries)." : null,
            deliveriesLeft = after.DeliveriesLeft,
            deliveriesLeftThisWeek = await Game.Run(SatisfierBridge.Allowances).ConfigureAwait(false),
            steps = statuses,
        };
    }

    private static unsafe void CloseWindow(string name)
    {
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)RetainerUi.Ptr(name).Address;
        if (addon != null) addon->Close(true);
    }

    /// <summary>Presses Accept on the rank-up rewards window (SatisfactionSupplyResult) when it is open.</summary>
    private static unsafe bool AcceptRankUp()
    {
        if (!RetainerUi.Ready("SatisfactionSupplyResult")) return false;
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)RetainerUi.Ptr("SatisfactionSupplyResult").Address;
        return VentureTools.Click(addon, addon->GetComponentButtonById(36));
    }

    private static SatisfierBridge.Client Find(string name)
    {
        var clients = SatisfierBridge.Clients();
        return clients.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? clients.FirstOrDefault(c => Game.Matches(c.Name, name))
               ?? throw new ToolException($"No custom delivery client named '{name}'. Clients: {string.Join(", ", clients.Select(c => c.Name))}.");
    }
}
