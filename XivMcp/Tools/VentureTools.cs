using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Retainer ventures: which venture brings an item (and which retainer can do it), and sending a retainer on a specific venture
/// through the summoning bell windows — the same steps AutoRetainer takes (menu → category → venture list → assign).
/// </summary>
internal static class VentureTools
{
    public const uint VentureToken = 21072;
    private const uint QuickExplorationId = 395;

    // Retainer menu texts (Addon sheet).
    private const uint AddonViewReport = 2385;     // "View venture report. (Complete)"
    private static readonly uint[] AddonAssign = [2386, 2387]; // "Assign venture."

    // Venture category menu texts (QuestDialogueText custom/000/CmnDefRetainerCall_00010).
    private const string CategorySheet = "custom/000/CmnDefRetainerCall_00010";
    private const uint CategoryHunting = 195, CategoryMining = 197, CategoryBotany = 199, CategoryFishing = 201, CategoryQuick = 402;

    public static IEnumerable<McpTool> Create(Configuration config, RetainerTracker tracker, PluginCompat compat)
    {
        void RequireEnabled()
        {
            if (!config.AllowItemsRetainers)
                throw new ToolException("Ventures are disabled. Enable \"Items & retainers\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "find_ventures",
            Description = "Retainer ventures that bring an item (hunting for monster parts, mining, botany, fishing): the venture, required retainer " +
                          "class and level, venture token cost, duration, quantity (and the gathering/perception or item level needed for bigger hauls, " +
                          "when 'Online lookups' is on), plus which of your retainers can take it right now (class, level, current venture). " +
                          "Without 'item': each retainer's current venture.",
            InputSchema = """
                { "type": "object", "properties": { "item": { "type": "string", "description": "Item name or id." } } }
                """,
            Handler = async (args, ct) =>
            {
                var query = args.String("item");
                JsonArray? teamcraft = null;
                Item? item = null;
                if (query is not null)
                {
                    item = await Game.Run(() => Items.Resolve(query)).ConfigureAwait(false);
                    if (config.AllowOnlineData) teamcraft = await Teamcraft.Sources(item.Value.RowId, ct).ConfigureAwait(false);
                }
                return await Game.Run<object?>(() =>
                {
                    var retainers = tracker.Get(Svc.PlayerState.ContentId)?.Retainers ?? [];
                    if (item is not { } it)
                        return new { retainers = retainers.Select(DescribeRetainer).ToList(), ventureTokens = Owned(VentureToken) };

                    var ventures = VenturesFor(it.RowId);
                    var tiers = teamcraft?.OfType<JsonObject>().FirstOrDefault(s => s["type"]?.GetValue<int>() == (int)Teamcraft.SourceType.Ventures)?["data"] as JsonArray;
                    return new
                    {
                        item = new { id = it.RowId, name = it.Name.ExtractText() },
                        ventureTokens = Owned(VentureToken),
                        ventures = ventures.Select(v => new
                        {
                            ventureId = v.Task.RowId,
                            name = VentureName(v.Task),
                            category = CategoryName(v.Task.ClassJobCategory.RowId),
                            retainerLevel = v.Task.RetainerLevel,
                            ventureCost = v.Task.VentureCost,
                            durationMinutes = v.Task.MaxTimemin,
                            requiredItemLevel = v.Task.RequiredItemLevel > 0 ? v.Task.RequiredItemLevel : (int?)null,
                            requiredGathering = v.Task.RequiredGathering > 0 ? v.Task.RequiredGathering : (int?)null,
                            quantities = tiers?.OfType<JsonObject>().FirstOrDefault(t => ItemSourceTools.UInt(t["id"]) == v.Task.RowId)?["quantities"]?.DeepClone()
                                         ?? (JsonNode)new JsonArray(v.Normal.Quantity.Select(q => (JsonNode?)JsonValue.Create((int)q)).ToArray()),
                            eligibleRetainers = retainers.Where(r => CanTake(r.ClassJob, r.Level, v.Task))
                                .Select(r => new { r.Name, job = JobAbbr(r.ClassJob), r.Level, busy = VentureState(r.VentureId, r.VentureComplete) }).ToList(),
                        }).ToList(),
                        none = ventures.Count == 0 ? "No retainer venture brings this item." : null,
                        retainerData = retainers.Count == 0 ? "No retainer list cached yet; open the retainer list at a summoning bell once." : null,
                    };
                }).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "assign_venture",
            Description = "Sends a retainer on a specific venture (by item it should bring, or venture id from find_ventures; venture_id 395 = quick " +
                          "exploration), to the named retainer or — without retainer — a free one that can take it. The retainer list must be open (interact " +
                          "with a summoning bell). A finished report is collected first (its items go to the retainer, as usual). Running ventures are " +
                          "never interrupted: if all suitable retainers are busy it says so and suggests which venture to recall (quick ones first, then " +
                          "the long 18-24 hour ones) — recalling is a separate step (recall_venture) that the player must approve in game. " +
                          "Costs venture tokens like assigning by hand. If AutoRetainer is installed it is paused meanwhile; note that its own venture " +
                          "settings apply again the next time it processes this retainer. Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "retainer": { "type": "string", "description": "Retainer name; omit to use a free retainer that can take the venture." },
                    "item": { "type": "string", "description": "Item the venture should bring (picks the venture matching the retainer's class)." },
                    "venture_id": { "type": "integer", "description": "Exact venture id (alternative to item)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var name = args.String("retainer");
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                var steps = new List<string>();
                try
                {
                    var plan = await Game.RunLoggedIn(() =>
                    {
                        InventoryActionTools.EnsureNotBusy();
                        if (!RetainerUi.RetainerListOpen && RetainerUi.ActiveRetainerName is null)
                            throw new ToolException("The retainer list is not open. Use a summoning bell first (navigate_to summoning_bell, then interact_with_object).");
                        var (retainer, task) = ChooseRetainer(name, args.String("item"), args.UInt("venture_id"));
                        if (Owned(VentureToken) < task.VentureCost)
                            throw new ToolException($"Not enough venture tokens: {task.VentureCost} needed, {Owned(VentureToken)} owned.");
                        compat.AcquireBell();
                        return (Retainer: retainer.Name, Task: task, HasReport: retainer.VentureId != 0, Previous: (uint)retainer.VentureId,
                                Category: CategoryFor(task, retainer.ClassJob));
                    }).ConfigureAwait(false);

                    await RetainerUi.OpenMenu(plan.Retainer, ct).ConfigureAwait(false);
                    steps.Add($"Opened {plan.Retainer}.");
                    await Assign(plan.Task, plan.Category, plan.HasReport, plan.Previous, steps, ct).ConfigureAwait(false);
                    await RetainerUi.Close(ct).ConfigureAwait(false);

                    var after = await Game.Run(() => FindRetainer(plan.Retainer)).ConfigureAwait(false);
                    if (after.VentureId != plan.Task.RowId)
                        throw new ToolException($"{plan.Retainer} did not take the venture (now on venture {after.VentureId}). Steps: {string.Join(" → ", steps)}");
                    return new
                    {
                        retainer = plan.Retainer,
                        venture = VentureName(plan.Task),
                        ventureId = plan.Task.RowId,
                        returns = DateTimeOffset.FromUnixTimeSeconds(after.VentureComplete).UtcDateTime,
                        ventureTokensLeft = await Game.Run(() => Owned(VentureToken)).ConfigureAwait(false),
                        autoRetainer = compat.AutoRetainerLoaded ? "AutoRetainer may assign this retainer differently the next time it processes it, according to its own settings." : null,
                        steps,
                    };
                }
                finally
                {
                    InventoryActionTools.Gate.Release();
                }
            },
        };
    }

    /// <summary>recall_venture: cancels a running venture, only after the player approved it in game.</summary>
    public static McpTool RecallTool(Configuration config, PluginCompat compat) => new()
    {
        Name = "recall_venture",
        Description = "Recalls a retainer from a running venture (the game's \"Recall\" in the venture report), so it can take another one. The " +
                      "venture's progress is lost. Never done on the assistant's own: every recall shows an approval popup in game and only " +
                      "happens when the player clicks Approve. Prefer recalling quick ventures before long 18-24 hour ones (assign_venture " +
                      "suggests an order). The retainer list must be open. Requires 'Retainer ventures' in /xivmcp.",
        InputSchema = """
            { "type": "object", "properties": { "retainer": { "type": "string", "description": "Retainer name." } }, "required": ["retainer"] }
            """,
        ReadOnly = false,
        Destructive = true,
        Handler = async (args, ct) =>
        {
            if (!config.AllowItemsRetainers)
                throw new ToolException("Ventures are disabled. Enable \"Items & retainers\" in the XIV MCP settings window (/xivmcp) in game.");
            var name = args.String("retainer") ?? throw new ToolException("'retainer' is required.");
            await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
            var steps = new List<string>();
            try
            {
                var target = await Game.RunLoggedIn(() =>
                {
                    InventoryActionTools.EnsureNotBusy();
                    if (!RetainerUi.RetainerListOpen && RetainerUi.ActiveRetainerName is null)
                        throw new ToolException("The retainer list is not open. Use a summoning bell first.");
                    var r = FindRetainer(name);
                    if (!r.Running) throw new ToolException($"{r.Name} is not on a running venture ({VentureState(r.VentureId, r.VentureComplete)}).");
                    var task = Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(r.VentureId);
                    return (Retainer: r, Venture: task is { } t ? VentureName(t) : $"venture {r.VentureId}", Quick: task is { } q && IsQuick(q));
                }).ConfigureAwait(false);

                await Consent.Require($"Recall {target.Retainer.Name} from their venture?",
                    [
                        $"{target.Retainer.Name} is on {target.Venture} ({(target.Quick ? "quick" : "long")} venture), {VentureState(target.Retainer.VentureId, target.Retainer.VentureComplete)}.",
                        "Recalling cancels the venture; its progress and rewards are lost.",
                    ], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                steps.Add("Approved in game.");

                await Game.Run(() => { compat.AcquireBell(); return true; }).ConfigureAwait(false);
                await RetainerUi.OpenMenu(target.Retainer.Name, ct).ConfigureAwait(false);
                steps.Add($"Opened {target.Retainer.Name}.");
                await Recall(steps, ct).ConfigureAwait(false);
                await RetainerUi.Close(ct).ConfigureAwait(false);

                var after = await Game.Run(() => FindRetainer(target.Retainer.Name)).ConfigureAwait(false);
                if (after.Running) throw new ToolException($"{target.Retainer.Name} is still on the venture. Steps: {string.Join(" → ", steps)}");
                return new { recalled = target.Retainer.Name, venture = target.Venture, steps, next = "assign_venture can send this retainer now." };
            }
            finally
            {
                InventoryActionTools.Gate.Release();
            }
        },
    };

    private const uint AddonRecallButton = 2365;   // "Recall"
    private const uint AddonRecallConfirm = 2371;  // "Cancel venture and recall this retainer?"

    /// <summary>From the retainer menu: open the venture report, click Recall, confirm. Ends back at the menu.</summary>
    private static async Task Recall(List<string> steps, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var lastAction = DateTime.MinValue;
        var stage = 0; // 0 = menu, 1 = report open, 2 = recall clicked, 3 = confirmed
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct).ConfigureAwait(false);
            if (DateTime.UtcNow - lastAction < TimeSpan.FromMilliseconds(700)) continue;
            var step = await Game.Run(() => RecallStep(ref stage)).ConfigureAwait(false);
            if (step == "done") return;
            if (step is null) continue;
            steps.Add(step);
            lastAction = DateTime.UtcNow;
        }
        throw new ToolException($"Timed out recalling. Steps so far: {string.Join(" → ", steps)}. Check the game windows.");
    }

    private static unsafe string? RecallStep(ref int stage)
    {
        if (RetainerUi.Ready("SelectYesno") && stage >= 2)
        {
            var yesno = (AddonSelectYesno*)Addon("SelectYesno");
            var text = yesno->PromptText == null ? "" : yesno->PromptText->NodeText.ToString();
            if (!SameStart(text, AddonText(AddonRecallConfirm)))
                throw new ToolException($"Unexpected confirmation: \"{text}\"; not answering it.");
            RetainerUi.Fire(&yesno->AtkUnitBase, true, 0);
            stage = 3;
            return "Confirmed the recall.";
        }

        if (RetainerUi.Ready("RetainerTaskResult") && stage <= 1)
        {
            var result = (AddonRetainerTaskResult*)Addon("RetainerTaskResult");
            stage = 1;
            var recall = AddonText(AddonRecallButton);
            foreach (var button in new[] { result->ReassignButton, result->ConfirmButton })
            {
                if (button == null || button->ButtonTextNode == null) continue;
                if (!button->ButtonTextNode->NodeText.ToString().Trim().Equals(recall, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Click(&result->AtkUnitBase, button)) return null;
                stage = 2;
                return "Clicked Recall.";
            }
            throw new ToolException("The venture report has no Recall button (the venture may have just finished).");
        }

        if (RetainerUi.Ready("Talk")) { RetainerUi.ClickTalk(); return null; }
        if (RetainerUi.MenuEntries() is null) return null;
        if (stage == 3) return "done";
        if (stage == 0)
        {
            foreach (var row in new uint[] { 2384, AddonViewReport, 2403 })
                if (RetainerUi.SelectMenuEntry(row)) return "Opening the venture report.";
            throw new ToolException($"No venture report entry in the menu: {string.Join(" | ", RetainerUi.MenuEntries()!)}");
        }
        return null;
    }

    private static string AddonText(uint row) => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(row)?.Text.ExtractText() ?? "";

    // ------------------------------------------------------------------ venture data

    private sealed record Venture(RetainerTask Task, RetainerTaskNormal Normal);

    private static List<Venture> VenturesFor(uint itemId)
    {
        var normals = Svc.Data.GetExcelSheet<RetainerTaskNormal>();
        return Svc.Data.GetExcelSheet<RetainerTask>()
            .Where(t => !t.IsRandom && t.Task.RowId != 0)
            .Select(t => (Task: t, Normal: normals.GetRowOrDefault(t.Task.RowId)))
            .Where(x => x.Normal is { } n && n.Item.RowId == itemId)
            .Select(x => new Venture(x.Task, x.Normal!.Value))
            .OrderBy(v => v.Task.RetainerLevel)
            .ToList();
    }

    private static RetainerTask PickVenture(byte classJob, byte level, string? item, uint? ventureId)
    {
        if (ventureId is { } id)
        {
            var task = Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(id) ?? throw new ToolException($"No venture {id}.");
            if (!CanTake(classJob, level, task))
                throw new ToolException($"This retainer ({JobAbbr(classJob)} {level}) can't take {VentureName(task)} ({CategoryName(task.ClassJobCategory.RowId)}, level {task.RetainerLevel}).");
            return task;
        }
        if (item is null) throw new ToolException("Give 'item' or 'venture_id'.");
        var it = Items.Resolve(item);
        var ventures = VenturesFor(it.RowId);
        if (ventures.Count == 0) throw new ToolException($"No venture brings {it.Name.ExtractText()}.");
        return ventures.Where(v => CanTake(classJob, level, v.Task)).Select(v => v.Task).LastOrDefault() is { RowId: not 0 } best
            ? best
            : throw new ToolException($"This retainer ({JobAbbr(classJob)} {level}) can't take any venture for {it.Name.ExtractText()}: " +
                                      string.Join(", ", ventures.Select(v => $"{CategoryName(v.Task.ClassJobCategory.RowId)} level {v.Task.RetainerLevel}")));
    }

    /// <summary>Venture categories: 17 = MIN, 18 = BTN, 19 = FSH, 34 = combat (hunting); quick exploration is open to all.</summary>
    private static uint RetainerCategory(byte classJob) => classJob switch { 16 => 17, 17 => 18, 18 => 19, _ => 34 };

    private static bool CanTake(byte classJob, byte level, RetainerTask task) =>
        classJob != 0 && level >= task.RetainerLevel && (task.RowId == QuickExplorationId || task.ClassJobCategory.RowId == RetainerCategory(classJob));

    private static uint CategoryFor(RetainerTask task, byte classJob) =>
        task.RowId == QuickExplorationId ? CategoryQuick
        : classJob switch { 16 => CategoryMining, 17 => CategoryBotany, 18 => CategoryFishing, _ => CategoryHunting };

    private static string CategoryName(uint category) => category switch { 17 => "mining", 18 => "botany", 19 => "fishing", 34 => "hunting", _ => $"category {category}" };

    private static string VentureName(RetainerTask task)
    {
        if (task.RowId == QuickExplorationId) return "Quick Exploration";
        if (task.IsRandom) return Svc.Data.GetExcelSheet<RetainerTaskRandom>().GetRowOrDefault(task.Task.RowId)?.Name.ExtractText() ?? $"Venture {task.RowId}";
        var normal = Svc.Data.GetExcelSheet<RetainerTaskNormal>().GetRowOrDefault(task.Task.RowId);
        return normal is { } n ? Items.Name(n.Item.RowId) : $"Venture {task.RowId}";
    }

    private static string? JobAbbr(byte job) => Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(job)?.Abbreviation.ExtractText();

    private static string VentureState(ushort ventureId, uint complete)
    {
        if (ventureId == 0) return "idle";
        var left = DateTimeOffset.FromUnixTimeSeconds(complete) - DateTimeOffset.UtcNow;
        return left <= TimeSpan.Zero ? "done (report waiting)" : $"running, back in {(int)left.TotalHours}h {left.Minutes}m";
    }

    private static object DescribeRetainer(RetainerSnapshot r) => new
    {
        r.Name,
        job = JobAbbr(r.ClassJob),
        r.Level,
        venture = r.VentureId == 0 ? null : Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(r.VentureId) is { } t ? VentureName(t) : $"Venture {r.VentureId}",
        state = VentureState(r.VentureId, r.VentureComplete),
    };

    private static unsafe long Owned(uint id)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(id);
    }

    private sealed record LiveRetainer(string Name, byte ClassJob, byte Level, ushort VentureId, uint VentureComplete)
    {
        public bool Running => VentureId != 0 && DateTimeOffset.FromUnixTimeSeconds(VentureComplete) > DateTimeOffset.UtcNow;
    }

    private static unsafe List<LiveRetainer> AllRetainers()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) throw new ToolException("Retainer data is not loaded. Open the retainer list at a summoning bell.");
        var list = new List<LiveRetainer>();
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0) list.Add(new LiveRetainer(r->NameString, r->ClassJob, r->Level, r->VentureId, r->VentureComplete));
        }
        return list;
    }

    /// <summary>
    /// The retainer to send (the named one, or a free one that can take the venture) and the venture. If every candidate is busy,
    /// fails with a suggestion which running venture to recall — quick ones first, then the long ones — without recalling anything.
    /// </summary>
    private static (LiveRetainer Retainer, RetainerTask Task) ChooseRetainer(string? name, string? item, uint? ventureId)
    {
        if (name is not null)
        {
            var named = FindRetainer(name);
            var task = PickVenture(named.ClassJob, named.Level, item, ventureId);
            if (named.Running) throw new ToolException($"{named.Name} is still on a venture ({VentureState(named.VentureId, named.VentureComplete)}). " + RecallAdvice([named]));
            return (named, task);
        }

        var candidates = new List<(LiveRetainer Retainer, RetainerTask Task)>();
        string? lastError = null;
        foreach (var r in AllRetainers().Where(r => r.ClassJob != 0))
        {
            try { candidates.Add((r, PickVenture(r.ClassJob, r.Level, item, ventureId))); }
            catch (ToolException ex) { lastError = ex.Message; }
        }
        if (candidates.Count == 0) throw new ToolException(lastError ?? "None of your retainers can take this venture.");
        // A free retainer, preferring one whose finished report is waiting anyway (it gets collected on the way).
        var free = candidates.Where(c => !c.Retainer.Running).OrderBy(c => c.Retainer.VentureId == 0 ? 1 : 0).ToList();
        if (free.Count > 0) return free[0];
        throw new ToolException($"All {candidates.Count} retainers that can take this venture are busy. " + RecallAdvice(candidates.Select(c => c.Retainer).ToList()));
    }

    /// <summary>Which running ventures to recall first: quick ones (short), then the long 18-24 hour ones. Nothing is recalled here.</summary>
    private static string RecallAdvice(List<LiveRetainer> busy)
    {
        var tasks = Svc.Data.GetExcelSheet<RetainerTask>();
        var ordered = busy.Where(r => r.Running)
            .Select(r => (r, Task: tasks.GetRowOrDefault(r.VentureId)))
            .OrderBy(x => x.Task is { } t && IsQuick(t) ? 0 : 1)
            .ThenBy(x => x.Task?.MaxTimemin ?? int.MaxValue)
            .Select(x => $"{x.r.Name} ({(x.Task is { } t ? VentureName(t) : $"venture {x.r.VentureId}")}, {(x.Task is { } t2 && IsQuick(t2) ? "quick" : "long")}, " +
                         $"{VentureState(x.r.VentureId, x.r.VentureComplete)})")
            .ToList();
        return ordered.Count == 0 ? "" :
            "To free one up, a running venture can be recalled with recall_venture — only with the player's explicit approval (it asks in game). " +
            "Suggested order, quick ventures first, then the long ones: " + string.Join("; ", ordered) + ".";
    }

    /// <summary>Quick ventures: quick exploration and the 1-hour targeted ventures (as opposed to 18-24 hour explorations).</summary>
    private static bool IsQuick(RetainerTask task) => task.RowId == QuickExplorationId || task.MaxTimemin <= 60;

    private static unsafe LiveRetainer FindRetainer(string name)
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) throw new ToolException("Retainer data is not loaded. Open the retainer list at a summoning bell.");
        LiveRetainer? fuzzy = null;
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0) continue;
            var live = new LiveRetainer(r->NameString, r->ClassJob, r->Level, r->VentureId, r->VentureComplete);
            if (live.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return live;
            if (live.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) fuzzy ??= live;
        }
        return fuzzy ?? throw new ToolException($"No retainer named '{name}'.");
    }

    // ------------------------------------------------------------------ the venture windows

    /// <summary>From the retainer's menu: collect a finished report if any, then assign the venture. Ends back at the menu.</summary>
    private static async Task Assign(RetainerTask task, uint category, bool hasReport, uint previous, List<string> steps, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        var lastAction = DateTime.MinValue;
        var reportHandled = !hasReport;
        var assigned = false;
        var tabSwitches = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(150, ct).ConfigureAwait(false);
            if (DateTime.UtcNow - lastAction < TimeSpan.FromMilliseconds(700)) continue;

            var step = await Game.Run(() => Step(task, category, ref reportHandled, ref assigned, ref tabSwitches, previous)).ConfigureAwait(false);
            if (step is null) continue;
            if (step == "done") return;
            steps.Add(step);
            lastAction = DateTime.UtcNow;
        }
        throw new ToolException($"Timed out assigning the venture. Steps so far: {string.Join(" → ", steps)}. Check the game windows.");
    }

    /// <summary>One tick of the venture dialogue. Returns a step description, "done", or null when nothing was done.</summary>
    private static unsafe string? Step(RetainerTask task, uint category, ref bool reportHandled, ref bool assigned, ref int tabSwitches, uint previous)
    {
        // Venture report: reassign if it is the same venture, otherwise confirm and pick from the menu.
        if (RetainerUi.Ready("RetainerTaskResult"))
        {
            var result = (AddonRetainerTaskResult*)Addon("RetainerTaskResult");
            reportHandled = true;
            if (previous == task.RowId && Click(&result->AtkUnitBase, result->ReassignButton)) return "Report collected, reassigning the same venture.";
            return Click(&result->AtkUnitBase, result->ConfirmButton) ? "Report collected." : null;
        }

        if (RetainerUi.Ready("RetainerTaskAsk"))
        {
            var ask = (AddonRetainerTaskAsk*)Addon("RetainerTaskAsk");
            if (!ask->AssignButton->IsEnabled) return null;
            assigned = Click(&ask->AtkUnitBase, ask->AssignButton);
            return assigned ? "Assigned." : null;
        }

        if (RetainerUi.Ready("RetainerTaskSupply"))
        {
            var supply = Addon("RetainerTaskSupply");
            if (supply->AtkValuesCount <= 107 || supply->AtkValues[3].Type == 0) return null; // still filling
            var count = supply->AtkValues[107].UInt;
            for (var i = 0; i < count && 42 + i < supply->AtkValuesCount; i++)
            {
                var ptr = supply->AtkValues[42 + i].Pointer;
                if (ptr != null && *(uint*)ptr == task.RowId)
                {
                    RetainerUi.Fire(supply, true, 5, i, null);
                    return $"Selected {VentureName(task)}.";
                }
            }
            // Not on this page: the list is grouped by retainer level in tabs of 5.
            if (++tabSwitches > 3) throw new ToolException($"{VentureName(task)} is not in this retainer's venture list (level, class or unlock missing?).");
            var tab = supply->AtkValues[40].Int - (task.RetainerLevel - 1) / 5 - 1;
            if (tab < 0) throw new ToolException($"{VentureName(task)} is not available to this retainer yet.");
            RetainerUi.Fire(supply, true, 4, tab, null);
            return $"Switched to the level {(task.RetainerLevel - 1) / 5 * 5 + 1}+ tab.";
        }

        if (RetainerUi.Ready("Talk")) { RetainerUi.ClickTalk(); return null; }

        var entries = RetainerUi.MenuEntries();
        if (entries is null) return null;

        // Back at the retainer menu after assigning: done.
        if (assigned) return "done";

        if (!reportHandled)
        {
            if (RetainerUi.SelectMenuEntry(AddonViewReport)) return "Opening the venture report.";
            reportHandled = true; // no report entry (e.g. already collected)
        }

        // The category menu ("Hunting", "Mining", ..., "Quick exploration") or the retainer menu ("Assign venture.").
        var categoryText = CategoryText(category);
        var catIndex = entries.FindIndex(e => SameStart(e, categoryText));
        if (catIndex >= 0)
        {
            RetainerUi.SelectMenuIndex(catIndex);
            return $"Chose {entries[catIndex]}.";
        }
        foreach (var row in AddonAssign)
            if (RetainerUi.SelectMenuEntry(row)) return "Assign venture.";
        throw new ToolException($"Unexpected menu: {string.Join(" | ", entries)}");
    }

    private static string CategoryText(uint row) =>
        Svc.Data.GetExcelSheet<QuestDialogueText>(name: CategorySheet)?.GetRowOrDefault(row)?.Value.ExtractText() ?? "";

    /// <summary>Menu texts carry a suffix like "(Requires 1 venture)"; compare the part before it.</summary>
    private static bool SameStart(string entry, string wanted)
    {
        static string Head(string s) => (s.IndexOf('(') is > 0 and var cut ? s[..cut] : s).Trim().TrimEnd('.', '。').Trim();
        var a = Head(entry);
        var b = Head(wanted);
        return a.Length > 0 && b.Length > 0 && (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    private static unsafe AtkUnitBase* Addon(string name) => Svc.GameGui.GetAddonByName<AtkUnitBase>(name, 1);

    /// <summary>Clicks a button component the way the game's own click handler receives it.</summary>
    internal static unsafe bool Click(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null || !button->IsEnabled) return false;
        var owner = button->AtkComponentBase.OwnerNode;
        if (owner == null || !owner->AtkResNode.IsVisible()) return false;
        var evt = owner->AtkResNode.AtkEventManager.Event;
        if (evt == null) return false;
        // Some windows (e.g. the retainer sell window's Confirm) read the event data, so never pass null: a zeroed block is what a
        // plain click without modifiers looks like.
        var data = stackalloc byte[sizeof(AtkEventData)];
        new Span<byte>(data, sizeof(AtkEventData)).Clear();
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, (AtkEventData*)data);
        return true;
    }
}

/// <summary>Row type for the game's custom quest/NPC dialogue sheets (Key, Value), e.g. custom/000/CmnDefRetainerCall_00010.</summary>
[Lumina.Excel.Sheet]
internal readonly struct QuestDialogueText(Lumina.Excel.ExcelPage page, uint offset, uint row) : Lumina.Excel.IExcelRow<QuestDialogueText>
{
    public Lumina.Excel.ExcelPage ExcelPage => page;
    public uint RowOffset => offset;
    public uint RowId => row;
    public Lumina.Text.ReadOnly.ReadOnlySeString Key => page.ReadString(offset, offset);
    public Lumina.Text.ReadOnly.ReadOnlySeString Value => page.ReadString(offset + 4, offset);
    public static QuestDialogueText Create(Lumina.Excel.ExcelPage page, uint offset, uint row) => new(page, offset, row);
}
