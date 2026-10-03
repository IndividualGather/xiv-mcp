using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Opening game windows (main menu commands) and interacting with nearby objects.</summary>
internal static class InteractionTools
{
    /// <summary>Main commands that do something other than opening a window.</summary>
    private static readonly HashSet<uint> BlockedCommands = [1 /*Stance*/, 23 /*Log Out*/, 24 /*Exit Game*/, 36 /*Return*/, 59 /*Ready Check*/, 73 /*Countdown*/, 79 /*Record Ready Check*/];

    /// <summary>Addons for main commands, to tell whether the window is already open (the command toggles).</summary>
    private static readonly Dictionary<uint, string[]> CommandAddons = new()
    {
        [2] = ["Character"], [3] = ["ActionMenu"], [4] = ["Journal"], [5] = ["ContentsInfo"], [6] = ["Achievement"],
        [7] = ["GatheringNoteBook"], [8] = ["MonsterNote"], [9] = ["RecipeNote"], [10] = ["Inventory", "InventoryLarge", "InventoryExpansion"],
        [11] = ["InventoryEvent"], [13] = ["FriendList"], [16] = ["AreaMap"], [17] = ["Emote"], [21] = ["Macro"], [25] = ["ArmouryBoard"],
        [27] = ["FreeCompany"], [29] = ["FishingNoteBook"], [33] = ["ContentsFinder"], [35] = ["Teleport"], [41] = ["FishGuide2"],
        [57] = ["LookingForGroup"], [60] = ["ContentsNote"], [61] = ["MountNoteBook"], [62] = ["MinionNoteBook"], [65] = ["GoldSaucerInfo"],
        [66] = ["Currency"], [67] = ["AetherCurrent"], [69] = ["OrchestrionPlayList"], [77] = ["InventoryBuddy"], [87] = ["Collection"],
        [89] = ["OrnamentNoteBook"], [93] = ["CharaCard"], [95] = ["GlassSelect"],
    };

    private const float MaxInteractDistance = 8f;

    public static IEnumerable<McpTool> Create(Configuration config, PluginCompat compat)
    {
        void RequireEnabled()
        {
            if (!config.AllowGameNavigation)
                throw new ToolException("Game interaction is disabled. Enable \"Game & navigation\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "list_windows",
            Description = "Lists the game windows open_window can open (the main menu commands: Achievements, Armoury Chest, Character, " +
                          "Chocobo Saddlebag, Currency, Mount Guide, Orchestrion List, Timers, ...), with whether each is unlocked and currently open.",
            Handler = (_, _) => Game.Run<object?>(() => Commands().Select(c => (object)new
            {
                id = c.RowId,
                name = c.Name.ExtractText(),
                unlocked = IsUnlocked(c.RowId),
                open = IsOpen(c.RowId),
            }).Concat(AgentWindows.Select(w => (object)new { id = (uint?)null, name = w.Key, unlocked = true, open = (bool?)AgentOpen(w.Value) })).ToList()),
        };

        yield return new McpTool
        {
            Name = "open_window",
            Description = "Opens a game window like clicking it in the main menu, e.g. \"Achievements\" (needed once per session before achievement " +
                          "progress can be read), \"Chocobo Saddlebag\", \"Armoury Chest\", \"Currency\", \"Timers\". Use list_windows for names. " +
                          "Does nothing if the window is already open. Use close_window to close it again. Requires 'Game & navigation' in /xivmcp.",
            InputSchema = """
                { "type": "object", "properties": { "window": { "type": "string", "description": "Window name (or main command id) from list_windows." } }, "required": ["window"] }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                if (AgentWindow(args.String("window")) is { } agentWindow)
                {
                    var shown = await Game.RunLoggedIn(() => { if (AgentOpen(agentWindow.Id)) return false; AgentShow(agentWindow.Id, true); return true; }).ConfigureAwait(false);
                    if (shown) await WaitFor(() => AgentOpen(agentWindow.Id), TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    return new { window = agentWindow.Name, action = shown ? "opened" : "already open", open = await Svc.Framework.RunOnFrameworkThread(() => AgentOpen(agentWindow.Id)).ConfigureAwait(false) };
                }
                var cmd = ResolveCommand(args.String("window"));
                var opened = await Game.RunLoggedIn(() =>
                {
                    if (IsOpen(cmd.RowId) == true) return false;
                    Execute(cmd.RowId);
                    return true;
                }).ConfigureAwait(false);
                var state = opened ? await WaitFor(() => IsOpen(cmd.RowId) != false, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false) : true;
                return new
                {
                    window = cmd.Name.ExtractText(),
                    action = opened ? "opened" : "already open",
                    open = await Svc.Framework.RunOnFrameworkThread(() => IsOpen(cmd.RowId)).ConfigureAwait(false),
                    note = cmd.RowId == 6 ? "Achievement progress is now loading; check_unlocks with category=achievement works in a moment." : null,
                };
            },
        };

        yield return new McpTool
        {
            Name = "close_window",
            Description = "Closes a game window: a main menu window (see list_windows), or a window that interact_with_object reported in " +
                          "openedWindows (e.g. FreeCompanyChest, SelectString). Requires 'Game & navigation' in /xivmcp.",
            InputSchema = """
                { "type": "object", "properties": { "window": { "type": "string", "description": "Window name / main command id from list_windows, or a window name from openedWindows." } }, "required": ["window"] }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                RequireEnabled();
                var window = args.String("window") ?? throw new ToolException("'window' is required.");
                var addon = InterestingAddons.FirstOrDefault(a => a.Equals(window, StringComparison.OrdinalIgnoreCase))
                            ?? (Svc.GameGui.GetAddonByName(window, 1) is { IsNull: false, IsVisible: true } ? window : null); // any open addon by its name (inspect_window lists them)
                if (addon is not null)
                    return Game.RunLoggedIn<object?>(() =>
                    {
                        if (Svc.GameGui.GetAddonByName(addon, 1) is not { IsNull: false, IsVisible: true }) return new { window = addon, action = "already closed" };
                        CloseAddon(addon);
                        return new { window = addon, action = "closed" };
                    });
                if (AgentWindow(window) is { } agentWindow)
                    return Game.RunLoggedIn<object?>(() =>
                    {
                        if (!AgentOpen(agentWindow.Id)) return new { window = agentWindow.Name, action = "already closed" };
                        AgentShow(agentWindow.Id, false);
                        return new { window = agentWindow.Name, action = "closed" };
                    });
                var cmd = ResolveCommand(window);
                return Game.RunLoggedIn<object?>(() =>
                {
                    var open = IsOpen(cmd.RowId);
                    if (open == false) return new { window = cmd.Name.ExtractText(), action = "already closed" };
                    if (open is null) throw new ToolException($"Can't tell whether {cmd.Name.ExtractText()} is open, so it is not toggled blindly. Close it in game.");
                    Execute(cmd.RowId);
                    return new { window = cmd.Name.ExtractText(), action = "closed" };
                });
            },
        };

        yield return new McpTool
        {
            Name = "interact_with_object",
            Description = "Targets and interacts with a nearby object, exactly like clicking it: summoning bell, company chest, voyage control panel, " +
                          "NPCs, aetherytes, ... Choose by name (nearest match) or gameObjectId from get_nearby_objects. The character does not move: " +
                          $"the object must be within about {MaxInteractDistance} yalms. Summoning bell: if AutoRetainer is installed it is paused while XIV MCP " +
                          "uses the bell (so it doesn't start processing ventures) and resumes when the bell is closed; YesAlready/TextAdvance are paused too. " +
                          "Returns which windows opened. Requires 'Game & navigation' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Object name (e.g. \"Summoning Bell\", \"Company Chest\", \"Voyage Control Panel\")." },
                    "game_object_id": { "type": "integer", "description": "Exact gameObjectId from get_nearby_objects (alternative to name)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var name = args.String("name");
                var id = args.Node("game_object_id") is { } n ? (ulong?)ulong.Parse(n.ToString()) : null;
                if (name is null && id is null) throw new ToolException("Give 'name' or 'game_object_id'.");

                var target = await Game.RunLoggedIn(() =>
                {
                    InventoryActionTools.EnsureNotBusy();
                    var obj = FindObject(name, id);
                    var distance = Game.DistanceToPlayer(obj.Position) ?? float.MaxValue;
                    if (distance > MaxInteractDistance)
                        throw new ToolException($"{obj.Name.TextValue} is {distance:0.#} yalms away; walk within ~{MaxInteractDistance} yalms first.");
                    var isBell = IsSummoningBell(obj);
                    if (isBell) compat.AcquireBell();
                    var before = VisibleAddons();
                    var conditionsBefore = Svc.Condition.AsReadOnlySet().ToHashSet();
                    ulong result;
                    unsafe
                    {
                        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
                        TargetSystem.Instance()->SetHardTarget(native, false, false, 0);
                        result = TargetSystem.Instance()->InteractWithObject(native, true);
                    }
                    return (Name: obj.Name.TextValue, Distance: distance, IsBell: isBell, Before: before, ConditionsBefore: conditionsBefore, Result: result);
                }).ConfigureAwait(false);

                // Report what the interaction opened.
                bool Reacted() => VisibleAddons().Except(target.Before).Any() || !Svc.Condition.AsReadOnlySet().SetEquals(target.ConditionsBefore);
                var reacted = await WaitFor(Reacted, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                if (!reacted)
                    throw new ToolException($"The game did not react to interacting with {target.Name} ({target.Distance:0.#} yalms away, result {target.Result}). " +
                                            "It is most likely out of interaction range or line of sight; move closer (within ~3 yalms is safe) and retry.");
                return await Svc.Framework.RunOnFrameworkThread(() => (object?)new
                {
                    interactedWith = target.Name,
                    distance = MathF.Round(target.Distance, 2),
                    openedWindows = VisibleAddons().Except(target.Before).ToList(),
                    conditions = Svc.Condition.AsReadOnlySet().Select(f => f.ToString()).Where(f => f != "NormalConditions").ToList(),
                    fcChest = target.Name.Contains("Chest", StringComparison.OrdinalIgnoreCase) && PluginCompat.FcchLoaded
                        ? "FCCH is installed: it may run its own on-open actions (as configured in FCCH). Use fc_chest_transfer to deposit or withdraw through it."
                        : null,
                    summoningBell = target.IsBell
                        ? new
                        {
                            retainerListOpen = RetainerUi.RetainerListOpen,
                            autoRetainer = compat.AutoRetainerLoaded ? "paused while XIV MCP uses the bell; resumes when the bell is closed" : "not installed",
                            next = "Use open_retainer / get_retainers / transfer_retainer_items; close_retainer with close_list=true closes the bell.",
                        }
                        : null,
                }).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "get_menu",
            Description = "Shows the choice menu the game currently displays after an interaction (e.g. the voyage control panel's " +
                          "\"Submersible management\" / \"Airship management\", or an NPC's options), and whether a dialogue text box is waiting for a click.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() => new
            {
                menu = RetainerUi.MenuEntries()?.Select((text, index) => new { index, text }).ToList(),
                dialogueWaiting = RetainerUi.Ready("Talk"),
                openWindows = VisibleAddons(),
            }),
        };

        yield return new McpTool
        {
            Name = "select_menu_option",
            Description = "Selects an entry of the currently open choice menu (see get_menu) by its text or index, like clicking it, " +
                          "or advances a waiting dialogue text box (advance_dialogue=true). Requires 'Game & navigation' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "option": { "type": "string", "description": "Entry text (case-insensitive, prefix match) or index from get_menu." },
                    "advance_dialogue": { "type": "boolean", "description": "Instead of a menu entry, click through the waiting dialogue text box." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                if (args.Bool("advance_dialogue", false))
                {
                    await Game.RunLoggedIn(() =>
                    {
                        if (!RetainerUi.Ready("Talk")) throw new ToolException("No dialogue is waiting.");
                        RetainerUi.ClickTalk();
                        return true;
                    }).ConfigureAwait(false);
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    return await Svc.Framework.RunOnFrameworkThread(() => (object?)new { advanced = true, openWindows = VisibleAddons() }).ConfigureAwait(false);
                }

                var option = args.String("option") ?? throw new ToolException("Give 'option' (text or index) or advance_dialogue=true.");
                var before = await Svc.Framework.RunOnFrameworkThread(VisibleAddons).ConfigureAwait(false);
                var selected = await Game.RunLoggedIn(() =>
                {
                    var entries = RetainerUi.MenuEntries() ?? throw new ToolException("No menu is open. Use interact_with_object first.");
                    var index = int.TryParse(option, out var i) ? i
                        : entries.FindIndex(e => e.Equals(option, StringComparison.OrdinalIgnoreCase)) is >= 0 and var exact ? exact
                        : entries.FindIndex(e => e.StartsWith(option, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) throw new ToolException($"No menu entry matches '{option}'. Entries: {string.Join(" | ", entries)}");
                    RetainerUi.SelectMenuIndex(index);
                    return entries[index];
                }).ConfigureAwait(false);
                await WaitFor(() => !VisibleAddons().SequenceEqual(before), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                return await Svc.Framework.RunOnFrameworkThread(() => (object?)new
                {
                    selected,
                    openWindows = VisibleAddons(),
                    menu = RetainerUi.MenuEntries(),
                }).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "load_game_data",
            Description = "Asks the game to load data that is normally only sent after opening its window, without opening anything: " +
                          "\"achievements\" (completed achievements) or \"titles\" (unlocked titles). Waits until the data arrived; the progress cache " +
                          "then captures it. Requires 'Game & navigation' in /xivmcp.",
            InputSchema = """
                { "type": "object", "properties": { "data": { "type": "string", "enum": ["achievements", "titles"] } }, "required": ["data"] }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var which = args.String("data")?.ToLowerInvariant();
                Func<bool> loaded = which switch
                {
                    "achievements" => () => Svc.Unlocks.IsAchievementListLoaded,
                    "titles" => () => Svc.Unlocks.IsTitleListLoaded,
                    _ => throw new ToolException("data must be 'achievements' or 'titles'."),
                };
                var already = await Game.RunLoggedIn(() =>
                {
                    if (loaded()) return true;
                    unsafe
                    {
                        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
                        if (which == "achievements") ui->Achievement.RequestCompletedAchievements();
                        else ui->TitleList.RequestTitleList();
                    }
                    return false;
                }).ConfigureAwait(false);
                var ok = already || await WaitFor(loaded, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                return new
                {
                    data = which,
                    loaded = ok,
                    action = already ? "was already loaded" : ok ? "loaded" : "requested, but the game has not answered within 10 seconds",
                    next = ok ? $"Use check_unlocks with category={(which == "titles" ? "title" : "achievement")}." : null,
                };
            },
        };

        yield return new McpTool
        {
            Name = "get_automation_status",
            Description = "Shows how XIV MCP cooperates with automation plugins: whether AutoRetainer, YesAlready and TextAdvance are loaded, " +
                          "AutoRetainer's busy / multi mode / suppressed state, and what XIV MCP currently pauses.",
            Handler = (_, _) => Game.Run<object?>(compat.Status),
        };
    }

    /// <summary>Windows that are not in the main menu but are opened through their game agent.</summary>
    private static readonly Dictionary<string, AgentId> AgentWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Titles"] = AgentId.CharacterTitle,
    };

    private static (string Name, AgentId Id)? AgentWindow(string? name) =>
        name is not null && AgentWindows.FirstOrDefault(w => w.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                                               (name.Length > 3 && w.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase))) is { Key: not null } hit
            ? (hit.Key, hit.Value) : null;

    private static unsafe bool AgentOpen(AgentId id) => AgentModule.Instance()->GetAgentByInternalId(id)->IsAgentActive();

    private static unsafe void AgentShow(AgentId id, bool show)
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(id);
        if (show) agent->Show(); else agent->Hide();
    }

    private static IEnumerable<MainCommand> Commands() =>
        Svc.Data.GetExcelSheet<MainCommand>().Where(c => !c.Name.IsEmpty && !string.IsNullOrWhiteSpace(c.Name.ExtractText()) && !BlockedCommands.Contains(c.RowId));

    private static MainCommand ResolveCommand(string? window)
    {
        if (window is null) throw new ToolException("'window' is required.");
        var commands = Commands().ToList();
        var match = uint.TryParse(window, out var id)
            ? commands.FirstOrDefault(c => c.RowId == id)
            : commands.FirstOrDefault(c => c.Name.ExtractText().Equals(window, StringComparison.OrdinalIgnoreCase));
        if (match.RowId == 0)
        {
            var fuzzy = commands.Where(c => Game.Matches(c.Name.ExtractText(), window)).ToList();
            if (fuzzy.Count == 1) match = fuzzy[0];
            else throw new ToolException(fuzzy.Count == 0
                ? $"Unknown window '{window}'. Use list_windows."
                : $"'{window}' is ambiguous: {string.Join(", ", fuzzy.Select(c => c.Name.ExtractText()))}");
        }
        return match;
    }

    private static unsafe void CloseAddon(string name) =>
        Svc.GameGui.GetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(name, 1)->Close(true);

    private static unsafe bool IsUnlocked(uint command) => UIModule.Instance()->IsMainCommandUnlocked(command);

    private static unsafe void Execute(uint command)
    {
        if (!IsUnlocked(command)) throw new ToolException("That window is not unlocked for this character.");
        UIModule.Instance()->ExecuteMainCommand(command);
    }

    /// <summary>True/false if known, null if XIV MCP can't tell for this window.</summary>
    private static bool? IsOpen(uint command) =>
        CommandAddons.TryGetValue(command, out var addons)
            ? addons.Any(a => Svc.GameGui.GetAddonByName(a, 1) is { IsNull: false, IsVisible: true })
            : null;

    private static readonly string[] InterestingAddons =
        ["RetainerList", "SelectString", "Talk", "FreeCompanyChest", "CompanyCraftSupply", "AirShipExplorationResult", "SubmersibleExplorationResult",
         "Shop", "SelectYesno", "SelectIconString", "Repair", "Teleport", "TelepotTown", "InventoryRetainer", "InventoryRetainerLarge", "ContentsTutorial",
         "AirShipExploration", "SubmersibleExploration", "CompanyCraftRecipeNoteBook", "HousingSignBoard", "MiragePrismPrismBox", "MiragePrismMiragePlate", "Cabinet"];

    private static List<string> VisibleAddons() =>
        InterestingAddons.Where(a => Svc.GameGui.GetAddonByName(a, 1) is { IsNull: false, IsVisible: true }).ToList();

    private static readonly Lazy<HashSet<uint>> BellIds = new(() =>
    {
        var names = Svc.Data.GetExcelSheet<EObjName>();
        var bell = names.GetRowOrDefault(2000401)?.Singular.ExtractText();
        return bell is null ? [2000401] : names.Where(e => e.Singular.ExtractText().Equals(bell, StringComparison.OrdinalIgnoreCase)).Select(e => e.RowId).ToHashSet();
    });

    public static bool IsSummoningBell(IGameObject obj) => BellIds.Value.Contains(obj.BaseId);

    private static IGameObject FindObject(string? name, ulong? id)
    {
        var self = Svc.Objects.LocalPlayer!;
        var candidates = Svc.Objects
            .Where(o => o.Address != self.Address && o.IsTargetable)
            .Where(o => id is { } i ? o.GameObjectId == i : o.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase) || Game.Matches(o.Name.TextValue, name))
            .OrderBy(o => o.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(o => System.Numerics.Vector3.Distance(o.Position, self.Position))
            .ToList();
        return candidates.FirstOrDefault()
               ?? throw new ToolException(id is not null ? $"No targetable object with id {id} nearby." : $"No targetable object named '{name}' nearby. Use get_nearby_objects.");
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await Svc.Framework.RunOnFrameworkThread(condition).ConfigureAwait(false)) return true;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return false;
    }
}
