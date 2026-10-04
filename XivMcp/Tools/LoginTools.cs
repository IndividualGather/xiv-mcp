using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Logging into other characters of the account — on any world and data center, with several service accounts — through the game's
/// own lobby (built in, no other plugin needed), and visiting other worlds (with Lifestream, or by asking the player and waiting).
/// </summary>
internal static class LoginTools
{
    public const string Lifestream = "Lifestream";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IEnumerable<McpTool> Create(Configuration config, CharacterRoster roster)
    {
        void RequireEnabled()
        {
            if (!config.AllowGameNavigation)
                throw new ToolException("Logging in is disabled. Enable \"Game & navigation\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "list_characters",
            Description = "The characters XIV MCP has seen: from the game's character list (lobby) and the character in game — name, home world, " +
                          "current world, data center, service account, and whether it is on the account in use now. Each data center's list is " +
                          "learned the first time it is shown (switch_character or refresh_character_list show it).",
            Handler = (_, _) => Game.Run<object?>(() =>
            {
                var current = Svc.ClientState.IsLoggedIn ? Svc.PlayerState.ContentId : 0;
                var account = roster.CurrentAccount;
                return new
                {
                    loggedIn = current == 0 ? null : new { name = Svc.Objects.LocalPlayer?.Name.TextValue, world = Excel.Name(Svc.PlayerState.CurrentWorld) },
                    characters = roster.All().Select(c => new
                    {
                        name = c.Name,
                        homeWorld = c.HomeWorld,
                        currentWorld = c.CurrentWorld,
                        visiting = c.HomeWorldId != c.CurrentWorldId ? true : (bool?)null,
                        dataCenter = c.DataCenter,
                        serviceAccount = c.ServiceAccount >= 0 ? c.ServiceAccount + 1 : (int?)null,
                        account = c.AccountKey is null || account is null ? "unknown" : c.AccountKey == account ? "this account" : "other account",
                        loggedIn = c.ContentId == current ? true : (bool?)null,
                        seen = c.SeenUtc,
                    }).ToList(),
                };
            }),
        };

        yield return new McpTool
        {
            Name = "switch_character",
            Description = "Logs out and into another character of the account — on any world or data center, on any of the account's service " +
                          "accounts — and waits until it is in game. Built in: it goes through the game's own lobby (title screen, service account, " +
                          "data center, world, character) and only confirms when the game's prompt names the right character. Characters XIV MCP " +
                          "hasn't seen in a character list yet need their home 'world' (and 'service_account' if there are several). Refused in " +
                          "duties, combat, and while crafting, gathering or multi-character retainer automation runs. Requires 'Game & navigation' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Character name (from list_characters)." },
                    "world": { "type": "string", "description": "Home world — to tell same-named characters apart, or for a character not seen yet." },
                    "service_account": { "type": "integer", "description": "Service account (1 = first) for a character not seen yet, if the account has several." },
                    "timeout_seconds": { "type": "integer", "description": "How long to wait for the character to be in game (default 300; login queues can take longer)." }
                  },
                  "required": ["name"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var name = args.String("name") ?? throw new ToolException("'name' is required.");
                var serviceAccount = args.Node("service_account") is null ? (int?)null : args.Int("service_account", 1, 1, 9) - 1;
                if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new ToolException("A character switch is already running.");
                var steps = new List<string>();
                try
                {
                    var target = await Game.Run(() => Resolve(name, args.String("world"), serviceAccount, roster)).ConfigureAwait(false);
                    if (target is null) return new { loggedIn = name, note = "Already logged in on this character." };
                    await Game.Run(() => { EnsureSafeToLogOut(); return true; }).ConfigureAwait(false);
                    var who = await LobbyLogin.Run(target, TimeSpan.FromSeconds(args.Int("timeout_seconds", 300, 60, 1800)), steps.Add, ct).ConfigureAwait(false);
                    return new { loggedIn = who, world = await Game.Run(() => Excel.Name(Svc.PlayerState.CurrentWorld)).ConfigureAwait(false), steps };
                }
                catch (ToolException ex)
                {
                    throw new ToolException($"{ex.Message} Steps: {(steps.Count == 0 ? "none" : string.Join(" → ", steps))}");
                }
                finally
                {
                    Gate.Release();
                }
            },
        };

        yield return new McpTool
        {
            Name = "refresh_character_list",
            Description = "Shows this account's character list for the current character's data center by logging out and straight back in, so " +
                          "XIV MCP learns every character there (and its service account). Same safety checks as switch_character. Requires " +
                          "'Game & navigation' in /xivmcp.",
            ReadOnly = false,
            Handler = async (_, ct) =>
            {
                RequireEnabled();
                if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new ToolException("A character switch is already running.");
                var steps = new List<string>();
                try
                {
                    var target = await Game.RunLoggedIn(() =>
                    {
                        EnsureSafeToLogOut();
                        var me = Svc.PlayerState.ContentId;
                        var known = roster.All().FirstOrDefault(c => c.ContentId == me);
                        var world = Svc.PlayerState.CurrentWorld.Value;
                        return new LobbyLogin.Target(Svc.Objects.LocalPlayer!.Name.TextValue, me, world.RowId, world.Name.ExtractText(), world.DataCenter.RowId,
                                                     known is { ServiceAccount: >= 0 } k ? k.ServiceAccount : null);
                    }).ConfigureAwait(false);
                    var who = await LobbyLogin.Run(target, TimeSpan.FromMinutes(5), steps.Add, ct, relog: true).ConfigureAwait(false);
                    await Task.Delay(2500, ct).ConfigureAwait(false); // let the roster record the list
                    var account = roster.CurrentAccount;
                    return new
                    {
                        loggedIn = who,
                        charactersOnThisAccount = roster.All().Where(c => c.AccountKey == account)
                            .Select(c => new { name = c.Name, homeWorld = c.HomeWorld, currentWorld = c.CurrentWorld, dataCenter = c.DataCenter,
                                               serviceAccount = c.ServiceAccount >= 0 ? c.ServiceAccount + 1 : (int?)null }).ToList(),
                        steps,
                    };
                }
                catch (ToolException ex)
                {
                    throw new ToolException($"{ex.Message} Steps: {(steps.Count == 0 ? "none" : string.Join(" → ", steps))}");
                }
                finally
                {
                    Gate.Release();
                }
            },
        };

        yield return new McpTool
        {
            Name = "visit_world",
            Description = "Travels the current character to another world (world visit on the same data center, or data center travel to " +
                          "another one) and waits until it arrived. With a travel plugin it travels by itself; otherwise XIV MCP asks the player to " +
                          "use World Visit or Data Center Travel and waits for them, so tell the player. Use switch_character to log into a " +
                          "different character instead. Requires 'Game & navigation' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "world": { "type": "string", "description": "Destination world (e.g. \"Shiva\"); your home world returns you home." },
                    "timeout_seconds": { "type": "integer", "description": "How long to wait (default 600; data center travel can queue)." }
                  },
                  "required": ["world"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var automatic = PluginCompat.IsLoaded(Lifestream);
                var worldName = args.String("world") ?? throw new ToolException("'world' is required.");
                if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new ToolException("A character switch or world travel is already running.");
                try
                {
                    var target = await Game.RunLoggedIn(() =>
                    {
                        var world = PublicWorld(worldName);
                        if (Svc.PlayerState.CurrentWorld.RowId == world.RowId) throw new ToolException($"Already on {world.Name.ExtractText()}.");
                        EnsureSafeToLogOut();
                        if (automatic) Svc.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(world.Name.ExtractText());
                        var current = Svc.Data.GetExcelSheet<World>().GetRowOrDefault(Svc.PlayerState.CurrentWorld.RowId);
                        return (world.RowId, Name: world.Name.ExtractText(), Dc: world.DataCenter.ValueNullable?.Name.ExtractText(),
                                SameDc: current?.DataCenter.RowId == world.DataCenter.RowId);
                    }).ConfigureAwait(false);
                    if (!automatic) XivMcp.Util.PlayerGuide.Ask(XivMcp.Maps.PlayerGuidance.VisitWorld(target.Name, target.Dc ?? "", target.SameDc));

                    var deadline = DateTime.UtcNow.AddSeconds(args.Int("timeout_seconds", 600, 30, 1800));
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                        var state = await Game.Run(() => (In: Svc.ClientState.IsLoggedIn && Svc.Objects.LocalPlayer is not null, World: Svc.PlayerState.CurrentWorld.RowId,
                                                          Busy: LifestreamBusy())).ConfigureAwait(false);
                        if (state.In && state.World == target.RowId && !state.Busy)
                            return new { arrived = target.Name, dataCenter = target.Dc, travelledBy = automatic ? "plugin" : "player" };
                    }
                    throw new ToolException(automatic
                        ? $"Not on {target.Name} after the timeout; the travel plugin may still be travelling or queued."
                        : $"Not on {target.Name} after the timeout. Ask the player whether they are still travelling, then try again.");
                }
                finally
                {
                    if (!automatic) XivMcp.Util.PlayerGuide.Done();
                    Gate.Release();
                }
            },
        };
    }

    // ------------------------------------------------------------------ planning

    /// <summary>The login target for a character, or null if it is the one in game. Framework thread.</summary>
    private static LobbyLogin.Target? Resolve(string name, string? world, int? serviceAccount, CharacterRoster roster)
    {
        var known = roster.All().Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (world is not null)
            known = known.Where(c => c.HomeWorld.Equals(world, StringComparison.OrdinalIgnoreCase) || c.CurrentWorld.Equals(world, StringComparison.OrdinalIgnoreCase)).ToList();
        if (known.Count > 1) throw new ToolException($"'{name}' exists on several worlds ({string.Join(", ", known.Select(k => k.HomeWorld))}); give 'world'.");

        var current = Svc.ClientState.IsLoggedIn ? Svc.PlayerState.ContentId : 0;
        var account = roster.CurrentAccount;
        if (known.FirstOrDefault() is { } c)
        {
            if (c.ContentId == current) return null;
            // Only characters of the account in use can be reached from the lobby; another account needs the launcher.
            if (account is not null && c.AccountKey is not null && c.AccountKey != account)
                throw new ToolException($"{c.Name} ({c.HomeWorld}) was seen on a different account than the one in use; switching accounts needs " +
                                        "logging out to the launcher and signing in with that account.");
            var w = Svc.Data.GetExcelSheet<World>().GetRow(c.CurrentWorldId);
            return new LobbyLogin.Target(c.Name, c.ContentId, w.RowId, w.Name.ExtractText(), w.DataCenter.RowId,
                                         serviceAccount ?? (c.ServiceAccount >= 0 ? c.ServiceAccount : null));
        }

        // Not seen yet: it is looked up in the lobby list of its home world (fails cleanly if it isn't on this account).
        if (world is null)
            throw new ToolException($"XIV MCP hasn't seen '{name}' in a character list yet; give its home 'world' " +
                                    "(and 'service_account' if your account has several).");
        var home = PublicWorld(world);
        return new LobbyLogin.Target(name, 0, home.RowId, home.Name.ExtractText(), home.DataCenter.RowId, serviceAccount);
    }

    private static World PublicWorld(string name)
    {
        var world = Svc.Data.GetExcelSheet<World>().FirstOrDefault(w => w.IsPublic && w.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase));
        return world.RowId != 0 ? world : throw new ToolException($"'{name}' is not a public world.");
    }

    private static void EnsureSafeToLogOut()
    {
        if (!Svc.ClientState.IsLoggedIn) return;
        if (Svc.Condition[ConditionFlag.BoundByDuty] || Svc.Condition[ConditionFlag.BoundByDuty56] || Svc.Condition[ConditionFlag.BoundByDuty95])
            throw new ToolException("Can't log out while in a duty.");
        if (Svc.Condition[ConditionFlag.InCombat]) throw new ToolException("Can't log out in combat.");
        if (Svc.Condition[ConditionFlag.Crafting] || Svc.Condition[ConditionFlag.Gathering]) throw new ToolException("Can't log out while crafting or gathering.");
        if (PluginCompat.Ipc<bool>("AutoRetainer.GetMultiModeEnabled") == true)
            throw new ToolException("AutoRetainer multi mode is enabled and switches characters itself; disable it first.");
        if (PluginCompat.IsLoaded("GatherbuddyReborn") && PluginCompat.Ipc<bool>("GatherBuddyReborn.IsAutoGatherEnabled") == true)
            throw new ToolException("GatherBuddy's auto-gather is running; stop it first.");
        if (PluginCompat.IsLoaded("Artisan") && (PluginCompat.Ipc<bool>("Artisan.IsBusy") == true || PluginCompat.Ipc<bool>("Artisan.IsListRunning") == true))
            throw new ToolException("Artisan is crafting; stop it first.");
        InventoryActionTools.EnsureNotBusy();
    }

    private static bool LifestreamBusy()
    {
        try { return PluginCompat.IsLoaded(Lifestream) && Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); }
        catch { return false; }
    }
}
