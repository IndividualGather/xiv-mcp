using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;
using XivMcp.TripleTriad;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// The Saucy integration: Triple Triad (opponents, cards, decks, playing and farming cards) and the Cactpot lotteries at the Gold
/// Saucer. Reading cards, opponents and decks works with TriadBuddy too; playing needs Saucy, which XIV MCP drives through its /saucy
/// command and its settings (see <see cref="SaucyBridge"/>). XIV MCP walks to the NPC and starts the conversation; Saucy plays.
/// </summary>
internal static class SaucyTools
{
    private const uint MgpItem = 29;
    private const uint MiniCactpotBroker = 1010445;
    private const uint JumboCactpotCashier = 1010451;
    private const int MaxFarmRuns = 200;

    public static IEnumerable<McpTool> Create(Func<JobManager> jobs, Func<string?> client)
    {
        yield return new McpTool
        {
            Name = "list_triad_npcs",
            Description = "Lists Triple Triad opponents (NPCs): where they stand (zone and map coordinates), their rules, match fee in MGP, the " +
                          "cards they can give with which ones the player has, whether the player has beaten them, and whether they can be " +
                          "challenged: some need an unlock quest first, some only play at certain Eorzea hours. Filter by name, zone, only " +
                          "opponents with cards still missing, or only those available now. Use the name (or id) with play_triple_triad, " +
                          "farm_triad_cards or build_triad_deck.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Part of the opponent's name." },
                    "zone": { "type": "string", "description": "Part of the zone's name, e.g. \"Gold Saucer\"." },
                    "missing_cards_only": { "type": "boolean", "description": "Only opponents that give cards the player doesn't have (default false)." },
                    "available_only": { "type": "boolean", "description": "Only opponents that can be challenged right now (default false)." },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 200, "description": "At most this many (default 50)." }
                  }
                }
                """,
            Handler = async (args, _) =>
            {
                var db = await Task.Run(TriadData.Get).ConfigureAwait(false);
                var name = args.String("name");
                var zone = args.String("zone");
                var npcs = db.Npcs.Where(n => (name is null || n.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                                              (zone is null || n.Zone.Contains(zone, StringComparison.OrdinalIgnoreCase))).ToList();
                var missingOnly = args.Bool("missing_cards_only", false);
                var availableOnly = args.Bool("available_only", false);
                var limit = args.Int("limit", 50, 1, 200);
                return await Game.Run(() =>
                {
                    var owned = TriadData.Owned(npcs.SelectMany(n => n.RewardCards).Distinct());
                    var bags = TriadData.InBags(npcs.SelectMany(n => n.RewardCards).Distinct().Select(c => db.Cards[c]));
                    var rows = npcs.Where(n => !missingOnly || n.RewardCards.Any(c => !owned.Contains(c) && !bags.Contains(c)))
                                   .Select(n => (Npc: n, Available: TriadData.Availability(n, owned)))
                                   .Where(r => !availableOnly || r.Available is { Unlocked: true, OpenNow: true }).ToList();
                    return new
                    {
                        count = rows.Count,
                        npcs = rows.Take(limit).Select(r => new
                        {
                            id = r.Npc.TriadId, r.Npc.Name, r.Npc.Zone, x = r.Npc.MapX, y = r.Npc.MapY, r.Npc.Rules, feeMgp = r.Npc.Fee,
                            beaten = TriadData.Beaten(r.Npc.TriadId),
                            unlocked = r.Available.Unlocked,
                            unlockQuests = r.Available.Unlocked ? null : r.Available.MissingQuests,
                            hours = r.Available.Hours,
                            availableNow = r.Available is { Unlocked: true, OpenNow: true },
                            cards = r.Npc.RewardCards.Select(c => CardInfo(db.Cards[c], owned, bags)).ToList(),
                        }).ToList(),
                    };
                }).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "list_triad_cards",
            Description = "Lists Triple Triad cards: stars, type, the numbers on each side, whether the player has the card (or has it in their bags " +
                          "waiting to be registered), and which opponents give it. Filter by owned / missing, part of a name, or an opponent; " +
                          "source=npc keeps only cards an opponent gives, with a summary of how many are missing from how many opponents and " +
                          "where. To collect those, use farm_triad_cards without an npc.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "filter": { "type": "string", "enum": ["all", "owned", "missing"], "description": "Which cards (default missing)." },
                    "source": { "type": "string", "enum": ["any", "npc"], "description": "npc: only cards Triple Triad opponents give (default any)." },
                    "name": { "type": "string", "description": "Part of the card's name." },
                    "npc": { "type": "string", "description": "Only cards this opponent gives (name or part of it)." },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 500, "description": "At most this many (default 100)." }
                  }
                }
                """,
            Handler = async (args, _) =>
            {
                var db = await Task.Run(TriadData.Get).ConfigureAwait(false);
                IEnumerable<TriadData.Card> cards = db.Cards.Values.OrderBy(c => c.Id);
                if (args.String("npc") is { } npcName)
                {
                    var npc = FindNpc(db, npcName);
                    cards = npc.RewardCards.Select(c => db.Cards[c]);
                }
                if (args.String("name") is { } name) cards = cards.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                var fromNpcs = args.String("source") == "npc";
                if (fromNpcs) cards = cards.Where(c => c.Npcs.Count > 0);
                var list = cards.ToList();
                var filter = args.String("filter") ?? "missing";
                var limit = args.Int("limit", 100, 1, 500);
                var npcNames = db.Npcs.ToDictionary(n => n.TriadId, n => n.Name);
                var npcZones = db.Npcs.ToDictionary(n => n.TriadId, n => n.Zone);
                return await Game.Run(() =>
                {
                    var owned = TriadData.Owned(list.Select(c => c.Id));
                    var bags = TriadData.InBags(list);
                    var rows = list.Where(c => filter switch { "owned" => owned.Contains(c.Id), "missing" => !owned.Contains(c.Id), _ => true }).ToList();
                    // For collecting: what is still to win from opponents (cards waiting in the bags are already won).
                    var toWin = fromNpcs ? rows.Where(c => !owned.Contains(c.Id) && !bags.Contains(c.Id)).ToList() : [];
                    return new
                    {
                        count = rows.Count,
                        summary = fromNpcs ? new
                        {
                            missingFromNpcs = toWin.Count,
                            opponents = toWin.SelectMany(c => c.Npcs).Distinct().Count(),
                            zones = toWin.SelectMany(c => c.Npcs).Distinct().Select(id => npcZones.GetValueOrDefault(id)).Where(z => z is not null)
                                         .GroupBy(z => z).OrderByDescending(g => g.Count()).Select(g => new { zone = g.Key, opponents = g.Count() }).ToList(),
                            waitingInBags = rows.Count(c => bags.Contains(c.Id) && !owned.Contains(c.Id)),
                        } : null,
                        ownedTotal = TriadData.Owned(db.Cards.Keys).Count,
                        cardsTotal = db.Cards.Count,
                        cards = rows.Take(limit).Select(c => new
                        {
                            c.Id, c.Name, c.Stars, c.Type, sides = new { top = c.Top, right = c.Right, bottom = c.Bottom, left = c.Left },
                            owned = owned.Contains(c.Id), inBags = bags.Contains(c.Id),
                            from = c.Npcs.Select(id => npcNames.GetValueOrDefault(id)).Where(n => n is not null).ToList(),
                        }).ToList(),
                    };
                }).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "get_triad_decks",
            Description = "The player's five saved Triple Triad decks: name and cards (with stars and sides). Saucy's optimized deck (build_triad_deck) is deck 5.",
            Handler = async (_, _) =>
            {
                var db = await Task.Run(TriadData.Get).ConfigureAwait(false);
                return await Game.RunLoggedIn(() => TriadData.Decks().Select((d, i) => new
                {
                    deck = i + 1,
                    name = d.Name,
                    complete = d.Cards.All(c => c != 0),
                    cards = d.Cards.Where(c => c != 0).Select(c => db.Cards.TryGetValue(c, out var card)
                        ? (object)new { card.Id, card.Name, card.Stars, sides = new { top = card.Top, right = card.Right, bottom = card.Bottom, left = card.Left } }
                        : new { Id = (int)c, Name = "?", Stars = 0 }).ToList(),
                }).ToList()).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "set_triad_deck",
            Description = "Saves five Triple Triad cards as one of the player's decks (1-5), replacing what is there, optionally with a name. " +
                          "The deck must follow the game's rules: 5 different cards the player has, at most one 5-star card and at most two of " +
                          "4-star or more. Cards by name or id (see list_triad_cards with filter owned).",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "deck": { "type": "integer", "minimum": 1, "maximum": 5, "description": "Which deck to replace." },
                    "cards": { "type": "array", "items": { "type": ["string", "integer"] }, "minItems": 5, "maxItems": 5, "description": "Five card names or ids." },
                    "name": { "type": "string", "description": "The deck's name (default: keep the current name)." }
                  },
                  "required": ["deck", "cards"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, _) =>
            {
                var db = await Task.Run(TriadData.Get).ConfigureAwait(false);
                var slot = args.Int("deck", 0, 1, 5);
                if (slot == 0) throw new ToolException("'deck' (1-5) is required.");
                var picked = (args.Node("cards")?.AsArray() ?? throw new ToolException("'cards' is required.")).Select(n =>
                {
                    var text = n?.ToString() ?? "";
                    if (int.TryParse(text, out var id))
                        return db.Cards.TryGetValue(id, out var byId) ? byId : throw new ToolException($"There is no card {id}.");
                    try { return NameMatch.Single(db.Cards.Values, text, c => c.Name); }
                    catch (ArgumentException ex) { throw new ToolException(ex.Message); }
                }).ToList();
                var name = args.String("name");
                return await Game.RunLoggedIn(() =>
                {
                    var owned = TriadData.Owned(picked.Select(c => c.Id));
                    if (TriadDeckRules.Problem(picked.Select(c => new TriadDeckCard(c.Id, c.Name, c.Stars, owned.Contains(c.Id))).ToList()) is { } problem)
                        throw new ToolException(problem);
                    TriadData.WriteDeck(slot - 1, picked.Select(c => c.Id).ToList(), name);
                    return new { deck = slot, name = TriadData.Decks()[slot - 1].Name, cards = picked.Select(c => new { c.Name, c.Stars }).ToList() };
                }).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "build_triad_deck",
            Description = "Builds the best deck against a Triple Triad opponent with Saucy's deck optimizer and saves it as deck 5 (named after " +
                          "the opponent). XIV MCP goes to the opponent and targets them, which starts the optimizer; it simulates many matches " +
                          "with the player's cards and the opponent's rules, for up to 'timeout_minutes'. Takes a while; best as a job step.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "npc": { "type": "string", "description": "The opponent's name (see list_triad_npcs)." },
                    "timeout_minutes": { "type": "integer", "minimum": 1, "maximum": 15, "description": "How long the optimizer may search (default 3). It keeps the best deck found by then." }
                  },
                  "required": ["npc"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var db = await Task.Run(TriadData.Get).ConfigureAwait(false);
                var npc = FindNpc(db, args.String("npc") ?? throw new ToolException("'npc' is required."));
                var minutes = args.Int("timeout_minutes", 3, 1, 15);
                await RequireSaucyIdle().ConfigureAwait(false);
                var steps = new List<string>();
                await NavigationTools.GoToNpc(npc.Spot, steps, ct).ConfigureAwait(false);

                using var settings = await Game.Run(() => SaucyBridge.Override(
                    ("UseSimmedDeck", true), ("AlwaysBuildOptimizedDeck", true), ("SkipOptimizedDeckForBeatenOrCompletedNpcs", false),
                    ("DeckOptimizerTimeoutMinutes", minutes))).ConfigureAwait(false);
                // The optimizer starts once per newly targeted opponent: clear the target, then target them.
                await Game.Run(() => { Svc.Targets.Target = null; return true; }).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
                await Game.Run(() => Target(npc.NpcId)).ConfigureAwait(false);
                Progress("build_triad_deck", $"Building a deck against {npc.Name}");

                var started = await WaitFor(() => SaucyBridge.OptimizerBusy, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                await WaitFor(() => !SaucyBridge.OptimizerBusy, TimeSpan.FromMinutes(minutes + 2), ct).ConfigureAwait(false);
                var deck = await Game.Run(() => TriadData.Decks()[4]).ConfigureAwait(false);
                var forThisNpc = deck.Name.Contains(npc.Name, StringComparison.OrdinalIgnoreCase);
                if (!forThisNpc)
                    throw new ToolException(started
                        ? "Saucy's optimizer ran, but deck 5 is not named after this opponent. Check deck 5 with get_triad_decks."
                        : $"Saucy's optimizer did not start for {npc.Name}. It doesn't run while a match window is open or while you are moving; try again.");
                return new
                {
                    npc = npc.Name, deck = 5, name = deck.Name,
                    cards = deck.Cards.Select(c => db.Cards.TryGetValue(c, out var card) ? new { card.Name, card.Stars } : new { Name = "?", Stars = 0 }).ToList(),
                    reused = !started ? "Saucy used a deck it had built before for this opponent." : null,
                    steps,
                };
            },
        };

        yield return new McpTool
        {
            Name = "play_triple_triad",
            Description = "Plays Triple Triad against an opponent with Saucy: XIV MCP goes to the NPC (teleporting and walking with Lifestream and " +
                          "vnavmesh, or showing the way) and starts the match; Saucy picks the deck, plays and rematches. Stops after 'matches' " +
                          "matches, or 'until' a card drops ('any_card'), the player has a given 'card', or all of the opponent's cards. A card in " +
                          "the bags waiting to be registered counts as had. Long-running: start it as a job step (farm_triad_cards does that). " +
                          "Match fees cost MGP.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "npc": { "type": "string", "description": "The opponent's name (see list_triad_npcs)." },
                    "triad_id": { "type": "integer", "description": "The opponent's id from list_triad_npcs, if two opponents share a name." },
                    "matches": { "type": "integer", "minimum": 1, "maximum": 500, "description": "With until=matches: how many matches (default 1)." },
                    "until": { "type": "string", "enum": ["matches", "any_card", "card", "all_cards"], "description": "When to stop (default matches)." },
                    "card": { "type": "string", "description": "With until=card: the card's name (it must be one this opponent gives)." },
                    "deck": { "type": ["string", "integer"], "description": "Which deck: 1-5, \"optimized\" (Saucy builds the best one into deck 5 first), or \"recommended\" (the game's pick). Default: Saucy's own setting." }
                  },
                  "required": ["npc"]
                }
                """,
            ReadOnly = false,
            Handler = (args, ct) => Play(args, ct),
        };

        yield return new McpTool
        {
            Name = "farm_triad_cards",
            Description = "Starts a background job that collects Triple Triad cards with Saucy. With 'npc': keeps playing that opponent until the " +
                          "player has a given 'card', or all of the opponent's cards. Without 'npc': collects every missing card opponents give, " +
                          "going from opponent to opponent (the current zone first, then zone by zone, nearest next), optionally only in one " +
                          "'zone' and for at most 'max_npcs' opponents. Opponents locked behind a quest are left out; with skip_unavailable " +
                          "(default true) also those outside their hours right now. Each step goes to the opponent, plays (Saucy rematches " +
                          "until a card drops) and checks after each drop. Follow it with get_job; skip a step or cancel with update_job / " +
                          "cancel_job. Dropped cards arrive in the bags and still need to be registered (used) to join the collection.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "npc": { "type": "string", "description": "One opponent's name (see list_triad_npcs). Leave out to collect from all opponents." },
                    "card": { "type": "string", "description": "With npc: the card to get. Leave out for all of the opponent's cards." },
                    "zone": { "type": "string", "description": "Without npc: only opponents in zones whose name contains this." },
                    "max_npcs": { "type": "integer", "minimum": 1, "maximum": 100, "description": "Without npc: at most this many opponents (default 20)." },
                    "skip_unavailable": { "type": "boolean", "description": "Without npc: leave out opponents outside their hours right now (default true)." },
                    "deck": { "type": ["string", "integer"], "description": "As in play_triple_triad: 1-5, \"optimized\" or \"recommended\"." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, _) =>
            {
                var db = await Task.Run(TriadData.Get).ConfigureAwait(false);
                if (args.String("npc") is not { } npcName) return await FarmAll(db, args, jobs(), client()).ConfigureAwait(false);
                var npc = FindNpc(db, npcName);
                await RequireUnlocked(npc).ConfigureAwait(false);
                var card = args.String("card");
                var goal = Goal(db, npc, card);
                var missing = await Game.Run(() => goal.Missing(TriadData.Owned(goal.Cards), TriadData.InBags(goal.Cards.Select(c => db.Cards[c])))).ConfigureAwait(false);
                if (missing.Count == 0) throw new ToolException(card is null ? $"You already have every card {npc.Name} gives." : $"You already have {db.Cards[goal.Cards[0]].Name}.");

                var stepArgs = new System.Text.Json.Nodes.JsonObject { ["npc"] = npc.Name, ["triad_id"] = npc.TriadId, ["until"] = card is null ? "all_cards" : "card" };
                if (card is not null) stepArgs["card"] = db.Cards[goal.Cards[0]].Name;
                if (args.Node("deck") is { } deck) stepArgs["deck"] = deck.DeepClone();
                var target = card is null ? $"{missing.Count} card{(missing.Count == 1 ? "" : "s")}" : db.Cards[goal.Cards[0]].Name;
                var job = jobs().Start($"Triple Triad: {target} from {npc.Name}",
                    [new JobManager.Step { Id = "farm", Tool = "play_triple_triad", Args = stepArgs, Note = $"Plays {npc.Name} until you have {target}" }],
                    client());
                return new
                {
                    started = JobManager.Describe(job), npc = npc.Name, npc.Zone,
                    missing = missing.Select(c => db.Cards[c].Name).ToList(), poll = $"get_job id={job.Id}",
                };
            },
        };

        yield return new McpTool
        {
            Name = "get_saucy_stats",
            Description = "Saucy's Triple Triad statistics: matches played, won, lost and drawn with Saucy, MGP won, cards dropped (with names), and " +
                          "whether Saucy is playing right now.",
            Handler = async (_, _) =>
            {
                var db = await Task.Run(TriadData.Get).ConfigureAwait(false);
                return await Game.Run(() =>
                {
                    var stats = SaucyBridge.Stats();
                    var won = (Dictionary<int, int>)stats["CardsWon"]!;
                    stats["CardsWon"] = won.Select(kv => new { card = db.Cards.TryGetValue(kv.Key, out var c) ? c.Name : kv.Key.ToString(), count = kv.Value }).ToList();
                    stats["PlayingNow"] = SaucyBridge.TriadRunning;
                    stats["OptimizingDeck"] = SaucyBridge.OptimizerBusy;
                    return stats;
                }).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "stop_saucy",
            Description = "Stops Saucy's Triple Triad automation and the travel it started (\"/saucy stop\"). A running play_triple_triad job step " +
                          "ends with what was played so far; cancel_job stops the job itself.",
            ReadOnly = false,
            Handler = async (_, _) =>
            {
                await Game.Run(() => { SaucyBridge.Command("stop"); return true; }).ConfigureAwait(false);
                return new { stopped = true };
            },
        };

        yield return new McpTool
        {
            Name = "play_mini_cactpot",
            Description = "Plays today's Mini Cactpot tickets (up to 3 a day, 10 MGP each) at the Gold Saucer: XIV MCP goes to the Mini Cactpot " +
                          "Broker and talks to them once per ticket; Saucy scratches the best spots. Ends when no ticket is left today.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "tickets": { "type": "integer", "minimum": 1, "maximum": 3, "description": "How many tickets to play at most (default 3)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var max = args.Int("tickets", 3, 1, 3);
                await RequireSaucyIdle().ConfigureAwait(false);
                var broker = TriadData.Locate(MiniCactpotBroker, "Mini Cactpot Broker") ?? throw new ToolException("The Mini Cactpot Broker was not found in the game data.");
                var steps = new List<string>();
                await NavigationTools.GoToNpc(broker, steps, ct).ConfigureAwait(false);
                var mgpBefore = await Game.Run(Mgp).ConfigureAwait(false);
                using var module = await Game.Run(() => SaucyBridge.EnableModule("MiniCactpot")).ConfigureAwait(false);
                var played = 0;
                for (var i = 0; i < max; i++)
                {
                    await Game.Run(() => Interact(MiniCactpotBroker)).ConfigureAwait(false);
                    // The board opens when a ticket is left; otherwise the broker only talks.
                    if (!await WaitFor(() => RetainerUi.Ready("LotteryDaily"), TimeSpan.FromSeconds(12), ct).ConfigureAwait(false))
                    {
                        await WaitFor(NoDialogue, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                        break;
                    }
                    Progress("play_mini_cactpot", $"Ticket {i + 1}");
                    await WaitFor(() => !RetainerUi.Ready("LotteryDaily") && NoDialogue(), TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
                    played++;
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
                var mgp = await Game.Run(Mgp).ConfigureAwait(false) - mgpBefore;
                return new
                {
                    tickets = played, mgpWon = mgp,
                    note = played < max ? "No more tickets today: the Mini Cactpot resets daily at 15:00 UTC." : null,
                    steps,
                };
            },
        };

        yield return new McpTool
        {
            Name = "play_jumbo_cactpot",
            Description = "The weekly Jumbo Cactpot at the Gold Saucer, done by Saucy: XIV MCP goes to the Jumbo Cactpot Cashier and talks to them; " +
                          "Saucy collects last week's prizes, walks to the Jumbo Cactpot Broker (with vnavmesh) and buys this week's tickets " +
                          "(up to 3 a week: 100, 150 and 200 MGP) with the 'numbers' (four digits each) or random ones. Without vnavmesh Saucy stops after the " +
                          "cashier; then talk to the broker yourself.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "numbers": { "type": "array", "items": { "type": "string" }, "maxItems": 3, "description": "Up to three 4-digit numbers, e.g. [\"0427\", \"1999\"]. Leave out for random numbers." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                IReadOnlyList<string> numbers;
                try { numbers = JumboTickets.Parse(args.Node("numbers")?.AsArray().Select(n => n?.ToString() ?? "") ?? []); }
                catch (ArgumentException ex) { throw new ToolException(ex.Message); }
                await RequireSaucyIdle().ConfigureAwait(false);
                var cashier = TriadData.Locate(JumboCactpotCashier, "Jumbo Cactpot Cashier") ?? throw new ToolException("The Jumbo Cactpot Cashier was not found in the game data.");
                var steps = new List<string>();
                await NavigationTools.GoToNpc(cashier, steps, ct).ConfigureAwait(false);
                var mgpBefore = await Game.Run(Mgp).ConfigureAwait(false);
                var settings = new List<(string, object?)> { ("JumboCactpot.NumberMode", numbers.Count > 0 ? 1 : 0) };
                for (var i = 0; i < 3; i++) settings.Add(($"JumboCactpot.Ticket{i + 1}Number", i < numbers.Count ? numbers[i] : ""));
                using var config = await Game.Run(() => SaucyBridge.Override([.. settings])).ConfigureAwait(false);
                using var module = await Game.Run(() => SaucyBridge.EnableModule("JumboCactpot")).ConfigureAwait(false);

                // Saucy does the rest: prizes at the cashier, the walk to the broker, the tickets. It is done once nothing of it
                // (dialogue, prize list, number pad, the walk) has been going for a while.
                await Game.Run(() => Interact(JumboCactpotCashier)).ConfigureAwait(false);
                steps.Add("Talking to the Jumbo Cactpot Cashier; Saucy takes over.");
                var padSeen = 0;
                long? mgpAtPad = null; // MGP when the number pad first opened: after the prizes, before the tickets
                var padOpen = false;
                var quietSince = DateTime.UtcNow;
                var deadline = DateTime.UtcNow.AddMinutes(5);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    var (pad, busy, mgp) = await Game.Run(() =>
                    {
                        var input = RetainerUi.Ready("LotteryWeeklyInput");
                        return (input, input || !NoDialogue() || RetainerUi.Ready("LotteryWeeklyRewardList") || SaucyBridge.JumboWalking, Mgp());
                    }).ConfigureAwait(false);
                    if (pad && !padOpen)
                    {
                        mgpAtPad ??= mgp;
                        Progress("play_jumbo_cactpot", $"Ticket {++padSeen}");
                    }
                    padOpen = pad;
                    if (busy) quietSince = DateTime.UtcNow;
                    else if (DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(8)) break;
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
                var mgpEnd = await Game.Run(Mgp).ConfigureAwait(false);
                // Ticket prices rise during the week (100, 150, 200), so the MGP spent tells how many were bought.
                var spent = mgpAtPad is { } atPad ? atPad - mgpEnd : 0;
                var tickets = JumboTickets.CountFromSpent(spent);
                var prizes = (mgpAtPad ?? mgpEnd) - mgpBefore;
                return new
                {
                    tickets, mgpSpent = spent, prizesMgp = prizes > 0 ? prizes : 0, numbers = numbers.Count > 0 ? numbers : null,
                    note = tickets == 0
                        ? "No ticket was bought: this week's tickets may be used up, or Saucy did not get to the broker (it walks there with vnavmesh). " +
                          "Prizes, if any, were collected at the cashier."
                        : null,
                    steps,
                };
            },
        };
    }

    // ---------------------------------------------------------------- playing

    private static async Task<object?> Play(ToolArgs args, CancellationToken ct)
    {
        var db = await Task.Run(TriadData.Get).ConfigureAwait(false);
        var triadId = args.UInt("triad_id");
        var npc = triadId is { } id && db.Npcs.FirstOrDefault(n => n.TriadId == id) is { } exact
            ? exact
            : FindNpc(db, args.String("npc") ?? throw new ToolException("'npc' is required."));
        await RequireUnlocked(npc).ConfigureAwait(false);
        var until = args.String("until") ?? "matches";
        var matches = args.Int("matches", 1, 1, 500);
        TriadFarmGoal? goal = until switch
        {
            "card" => Goal(db, npc, args.String("card") ?? throw new ToolException("until=card needs 'card'.")),
            "all_cards" => Goal(db, npc, null),
            "matches" or "any_card" => null,
            _ => throw new ToolException("'until' must be matches, any_card, card or all_cards."),
        };
        (int[] Owned, int[] Bags) Have() => goal is null ? ([], []) :
            (TriadData.Owned(goal.Cards).ToArray(), TriadData.InBags(goal.Cards.Select(c => db.Cards[c])).ToArray());
        if (goal is not null)
        {
            var (owned, bags) = await Game.Run(Have).ConfigureAwait(false);
            if (goal.IsMet(owned, bags)) return new { npc = npc.Name, played = 0, note = "You already have the card(s) this was after." };
        }

        await RequireSaucyIdle().ConfigureAwait(false);
        var steps = new List<string>();
        await NavigationTools.GoToNpc(npc.Spot, steps, ct).ConfigureAwait(false);
        using var deck = await Game.Run(() => DeckSettings(args.Node("deck")?.ToString())).ConfigureAwait(false);
        var before = await Game.Run(SaucyBridge.Stats).ConfigureAwait(false);

        var runs = 0;
        var totalMatches = 0;
        try
        {
            while (true)
            {
                runs++;
                // One Saucy run: a fixed number of matches, or rematches until a card drops (also for a card / all-cards goal,
                // which XIV MCP checks itself after each drop, counting cards in the bags).
                var command = until == "matches" ? $"tt play {matches}" : "tt cards any";
                await Game.Run(() =>
                {
                    SaucyBridge.Command(command);
                    SaucyBridge.Command("tt go");
                    Interact(npc.NpcId);
                    return true;
                }).ConfigureAwait(false);
                steps.Add(runs == 1 ? $"Started playing {npc.Name}." : $"Playing {npc.Name} again (run {runs}).");

                var played = await WaitForRun(npc.Name, ct).ConfigureAwait(false);
                totalMatches += played;
                if (played == 0) throw new ToolException($"Saucy stopped before a match began. Is {npc.Name} close enough to talk to, and is the match available (some opponents need a quest or a time of day)?");
                if (goal is null) break;
                var (owned, bags) = await Game.Run(Have).ConfigureAwait(false);
                Progress("play_triple_triad", $"{totalMatches} matches; {goal.Missing(owned, bags).Count} card(s) still missing");
                if (goal.IsMet(owned, bags)) break;
                if (runs >= MaxFarmRuns) throw new ToolException($"Still missing cards after {runs} runs; stopping so the job can be checked.");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await Game.Run(() => { SaucyBridge.Command("stop"); return true; }).ConfigureAwait(false);
            throw;
        }

        var after = await Game.Run(SaucyBridge.Stats).ConfigureAwait(false);
        int Diff(string key) => Convert.ToInt32(after[key]) - Convert.ToInt32(before[key]);
        var wonBefore = (Dictionary<int, int>)before["CardsWon"]!;
        var cards = ((Dictionary<int, int>)after["CardsWon"]!).Where(kv => kv.Value > wonBefore.GetValueOrDefault(kv.Key))
            .Select(kv => db.Cards.TryGetValue(kv.Key, out var c) ? c.Name : kv.Key.ToString()).ToList();
        var bagsNow = await Game.Run(() => TriadData.InBags(npc.RewardCards.Select(c => db.Cards[c]))).ConfigureAwait(false);
        return new
        {
            npc = npc.Name,
            played = Diff("GamesPlayedWithSaucy"), won = Diff("GamesWonWithSaucy"), lost = Diff("GamesLostWithSaucy"), drawn = Diff("GamesDrawnWithSaucy"),
            mgpWon = Diff("MGPWon"), cardsDropped = cards,
            toRegister = bagsNow.Select(c => db.Cards[c].Name).ToList(),
            note = bagsNow.Count > 0 ? "Cards in your bags join the collection once you use (register) them." : null,
            steps,
        };
    }

    /// <summary>Waits until Saucy's run ends; returns the matches it finished (sampled, as Saucy resets the count when it ends).</summary>
    private static async Task<int> WaitForRun(string npc, CancellationToken ct)
    {
        var started = await WaitFor(() => SaucyBridge.TriadRunning, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (!started) throw new ToolException("Saucy did not start playing.");
        var most = 0;
        var lastReported = -1;
        var deadline = DateTime.UtcNow.AddHours(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var (running, done) = await Game.Run(() => (SaucyBridge.TriadRunning, SaucyBridge.MatchesThisRun)).ConfigureAwait(false);
            most = Math.Max(most, done);
            if (most != lastReported)
            {
                Progress("play_triple_triad", $"{most} match{(most == 1 ? "" : "es")} against {npc}");
                lastReported = most;
            }
            if (!running) return Math.Max(most, await Game.Run(() => SaucyBridge.MatchesThisRun).ConfigureAwait(false));
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new ToolException("Saucy has been playing for 6 hours without finishing; stopping here.");
    }

    /// <summary>Saucy settings for the chosen deck, put back after the call.</summary>
    private static IDisposable DeckSettings(string? deck) => deck?.Trim().ToLowerInvariant() switch
    {
        null or "" => SaucyBridge.Override(),
        "optimized" or "optimised" => SaucyBridge.Override(("UseSimmedDeck", true), ("AlwaysBuildOptimizedDeck", true), ("SkipOptimizedDeckForBeatenOrCompletedNpcs", false)),
        "recommended" => SaucyBridge.Override(("UseSimmedDeck", false), ("SelectedDeckIndex", -2)),
        var n when int.TryParse(n, out var slot) && slot is >= 1 and <= 5 => SaucyBridge.Override(("UseSimmedDeck", false), ("SelectedDeckIndex", slot - 1)),
        _ => throw new ToolException("'deck' must be 1-5, \"optimized\" or \"recommended\"."),
    };

    /// <summary>
    /// farm_triad_cards without an npc: one job through every opponent that still gives missing cards, in a route that keeps travel
    /// short. Locked opponents (quest) are left out, and with skip_unavailable those outside their hours.
    /// </summary>
    private static async Task<object> FarmAll(TriadData.Db db, ToolArgs args, JobManager jobs, string? client)
    {
        var zone = args.String("zone");
        var maxNpcs = args.Int("max_npcs", 20, 1, 100);
        var skipUnavailable = args.Bool("skip_unavailable", true);
        var candidates = db.Npcs.Where(n => n.RewardCards.Count > 0 && (zone is null || n.Zone.Contains(zone, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count == 0) throw new ToolException(zone is null ? "No Triple Triad opponent gives cards." : $"No Triple Triad opponent with cards in a zone matching '{zone}'.");

        var (stops, skipped, territory, position) = await Game.RunLoggedIn(() =>
        {
            var allCards = candidates.SelectMany(n => n.RewardCards).Distinct().ToList();
            var owned = TriadData.Owned(allCards);
            var bags = TriadData.InBags(allCards.Select(c => db.Cards[c]));
            var stops = new List<TriadStop>();
            var skipped = new List<object>();
            foreach (var n in candidates)
            {
                var missing = n.RewardCards.Where(c => !owned.Contains(c) && !bags.Contains(c)).ToList();
                if (missing.Count == 0) continue;
                var a = TriadData.Availability(n, owned);
                if (!a.Unlocked) skipped.Add(new { npc = n.Name, n.Zone, reason = $"Needs a quest first: {string.Join(" or ", a.MissingQuests)}." });
                else if (skipUnavailable && !a.OpenNow) skipped.Add(new { npc = n.Name, n.Zone, reason = $"Only plays {a.Hours}." });
                else stops.Add(new TriadStop(n.TriadId, n.Name, n.Spot.Territory, n.Zone, n.Spot.Position, missing));
            }
            return (stops, skipped, (uint)Svc.ClientState.TerritoryType, Svc.Objects.LocalPlayer?.Position ?? default);
        }).ConfigureAwait(false);

        var route = TriadFarmPlan.Order(stops, territory, position, maxNpcs);
        if (route.Count == 0)
            throw new ToolException(skipped.Count > 0
                ? $"Every opponent with missing cards is unavailable right now ({skipped.Count}); see list_triad_npcs for their quests and hours."
                : "You have every card Triple Triad opponents give" + (zone is null ? "." : $" in zones matching '{zone}'."));
        var left = TriadFarmPlan.Order(stops, territory, position).Sum(s => s.Missing.Count) - route.Sum(s => s.Missing.Count);

        var deck = args.Node("deck");
        var steps = route.Select((s, i) =>
        {
            var stepArgs = new System.Text.Json.Nodes.JsonObject { ["npc"] = s.Name, ["triad_id"] = s.Id, ["until"] = "all_cards" };
            if (deck is not null) stepArgs["deck"] = deck.DeepClone();
            return new JobManager.Step
            {
                Id = $"npc{i + 1}", Tool = "play_triple_triad", Args = stepArgs,
                Note = $"{s.Name} ({s.Zone}): {string.Join(", ", s.Missing.Select(c => db.Cards[c].Name))}",
            };
        }).ToList();
        var cards = route.Sum(s => s.Missing.Count);
        var job = jobs.Start($"Triple Triad: {cards} card{(cards == 1 ? "" : "s")} from {route.Count} opponent{(route.Count == 1 ? "" : "s")}", steps, client);
        return new
        {
            started = JobManager.Describe(job),
            route = route.Select(s => new { npc = s.Name, s.Zone, cards = s.Missing.Select(c => db.Cards[c].Name).ToList() }).ToList(),
            skipped = skipped.Count > 0 ? skipped : null,
            notInThisJob = left > 0 ? $"{left} more missing card(s) from opponents beyond max_npcs; start another job once this one is done." : null,
            poll = $"get_job id={job.Id}",
        };
    }

    /// <summary>Refuses an opponent that needs an unlock quest first, naming the quest.</summary>
    private static async Task RequireUnlocked(TriadData.Npc npc)
    {
        var (unlocked, quests) = await Game.Run(() =>
        {
            var a = TriadData.Availability(npc, TriadData.Owned(npc.RewardCards));
            return (a.Unlocked, a.MissingQuests);
        }).ConfigureAwait(false);
        if (!unlocked) throw new ToolException($"{npc.Name} can't be challenged yet: finish {string.Join(" or ", quests)} first.");
    }

    // ---------------------------------------------------------------- helpers

    private static TriadData.Npc FindNpc(TriadData.Db db, string name)
    {
        try { return NameMatch.Single(db.Npcs, name, n => n.Name); }
        catch (ArgumentException ex) { throw new ToolException($"{ex.Message} (Triple Triad opponents: see list_triad_npcs.)"); }
    }

    private static TriadFarmGoal Goal(TriadData.Db db, TriadData.Npc npc, string? card)
    {
        try
        {
            if (card is null) return TriadFarmGoal.AllCards(npc.RewardCards);
            var match = NameMatch.Single(npc.RewardCards.Select(c => db.Cards[c]), card, c => c.Name);
            return TriadFarmGoal.ForCard(match.Id, npc.RewardCards);
        }
        catch (ArgumentException ex)
        {
            var gives = string.Join(", ", npc.RewardCards.Select(c => db.Cards[c].Name));
            throw new ToolException($"{ex.Message} {npc.Name} gives: {(gives.Length > 0 ? gives : "no cards")}.");
        }
    }

    private static object CardInfo(TriadData.Card c, HashSet<int> owned, HashSet<int> bags) =>
        new { c.Name, c.Stars, owned = owned.Contains(c.Id), inBags = bags.Contains(c.Id) };

    private static async Task RequireSaucyIdle()
    {
        var (loaded, running) = await Game.Run(() => SaucyBridge.Loaded ? (true, SaucyBridge.TriadRunning) : (false, false)).ConfigureAwait(false);
        if (!loaded) throw new ToolException("This needs Saucy. Install it from https://love.puni.sh/ment.json (add it under Dalamud settings → Experimental → Custom Plugin Repositories).");
        if (running) throw new ToolException("Saucy is already playing Triple Triad. Wait for it to finish, or use stop_saucy.");
    }

    private static long Mgp()
    {
        unsafe
        {
            var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            return im == null ? 0 : im->GetInventoryItemCount(MgpItem);
        }
    }

    private static bool NoDialogue() =>
        !RetainerUi.Ready("Talk") && !RetainerUi.Ready("SelectString") && !RetainerUi.Ready("SelectYesno") && !RetainerUi.Ready("SelectIconString");

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject NearbyNpc(uint npcId) =>
        Svc.Objects.Where(o => o.BaseId == npcId).OrderBy(o => Game.DistanceToPlayer(o.Position) ?? float.MaxValue).FirstOrDefault()
        ?? throw new ToolException("The NPC is not nearby any more.");

    private static bool Target(uint npcId)
    {
        Svc.Targets.Target = NearbyNpc(npcId);
        return true;
    }

    private static bool Interact(uint npcId)
    {
        var obj = NearbyNpc(npcId);
        unsafe
        {
            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->SetHardTarget(native, false, false, 0);
            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(native, true);
        }
        return true;
    }

    /// <summary>Polls a condition on the framework thread until it holds (true) or the time is up (false).</summary>
    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await Game.Run(condition).ConfigureAwait(false)) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static void Progress(string tool, string text) => JobManager.Instance?.StepProgress(tool, text);
}
