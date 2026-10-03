using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Artisan (crafting) and GatherBuddy Reborn (auto gathering). Running things goes through their IPC; their lists have no IPC, so
/// lists are edited in their own config files with the plugin briefly unloaded (see PluginTools.ModifyPluginJson).
/// </summary>
internal static class CraftGatherTools
{
    internal const string Artisan = "Artisan";
    internal const string GatherBuddy = "GatherbuddyReborn";
    internal const string GatherListsFile = "GatherbuddyReborn/auto_gather_lists.json";

    public static bool ArtisanLoaded => PluginCompat.IsLoaded(Artisan);
    public static bool GatherBuddyLoaded => PluginCompat.IsLoaded(GatherBuddy);

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        void RequireEnabled()
        {
            if (!config.AllowCraftingGathering)
                throw new ToolException("Crafting & gathering automation is disabled. Enable it in the XIV MCP settings window (/xivmcp) in game.");
        }

        // ================================================================== Artisan

        yield return new McpTool
        {
            Name = "get_crafting_lists",
            Description = "Artisan's crafting lists (id, name, recipes with item, job and quantity) and Artisan's state: list running / paused, " +
                          "Endurance mode, busy. Requires Artisan.",
            Handler = (_, _) => Game.Run<object?>(() =>
            {
                RequireArtisan();
                var lists = PluginTools.ReadPluginJson(Artisan, null)["NewCraftingLists"] as JsonArray ?? [];
                return new
                {
                    state = ArtisanState(),
                    lists = lists.OfType<JsonObject>().Select(l => new
                    {
                        id = l["ID"]?.GetValue<int>(),
                        name = l["Name"]?.ToString(),
                        recipes = (l["Recipes"] as JsonArray ?? []).OfType<JsonObject>().Select(r => DescribeRecipe(r["ID"]?.GetValue<uint>() ?? 0, r["Quantity"]?.GetValue<int>() ?? 0)).ToList(),
                    }).ToList(),
                };
            }),
        };

        yield return new McpTool
        {
            Name = "craft_item",
            Description = "Crafts an item a number of times with Artisan (like Artisan's \"craft X\"), by item name/id or recipe id. If several jobs " +
                          "can craft it, the current job's recipe is preferred. Requires Artisan and 'Crafting & gathering'.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string", "description": "Item name or id to craft." },
                    "recipe_id": { "type": "integer", "description": "Exact recipe id (alternative to item)." },
                    "amount": { "type": "integer", "description": "How many crafts (default 1)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                RequireEnabled();
                var amount = args.Int("amount", 1, 1, 9999);
                return Game.RunLoggedIn<object?>(() =>
                {
                    RequireArtisan();
                    EnsureArtisanIdle();
                    var recipe = ResolveRecipe(args.String("item"), args.UInt("recipe_id"));
                    Svc.PluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem").InvokeAction((ushort)recipe.RowId, amount);
                    return new { crafting = DescribeRecipe(recipe.RowId, amount), note = "Artisan takes over now; use crafting_control action=stop to stop it." };
                });
            },
        };

        yield return new McpTool
        {
            Name = "crafting_control",
            Description = "Controls Artisan: start a crafting list (by id or name), pause / resume the running list, or stop. Requires Artisan and " +
                          "'Crafting & gathering'.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "action": { "type": "string", "enum": ["start_list", "pause", "resume", "stop"] },
                    "list": { "type": "string", "description": "For start_list: list id or name." }
                  },
                  "required": ["action"]
                }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                RequireEnabled();
                var action = args.String("action")?.ToLowerInvariant();
                var listArg = args.String("list");
                return Game.RunLoggedIn<object?>(() =>
                {
                    RequireArtisan();
                    switch (action)
                    {
                        case "start_list":
                        {
                            var list = FindList(listArg ?? throw new ToolException("start_list needs 'list'."));
                            EnsureArtisanIdle();
                            if (Svc.PluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus").InvokeFunc())
                                throw new ToolException("Artisan's Endurance mode is on; lists can't start while it is active.");
                            Svc.PluginInterface.GetIpcSubscriber<int, object>("Artisan.StartListById").InvokeAction(list.Id);
                            return new { started = list.Name, id = list.Id };
                        }
                        case "pause":
                        case "resume":
                            if (!Svc.PluginInterface.GetIpcSubscriber<bool>("Artisan.IsListRunning").InvokeFunc())
                                throw new ToolException("No crafting list is running.");
                            Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetListPause").InvokeAction(action == "pause");
                            return new { action, state = ArtisanState() };
                        case "stop":
                            Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest").InvokeAction(true);
                            return new { action, state = ArtisanState() };
                        default:
                            throw new ToolException("action must be start_list, pause, resume or stop.");
                    }
                });
            },
        };

        yield return new McpTool
        {
            Name = "set_crafting_list",
            Description = "Creates or edits an Artisan crafting list: name and recipes as [{ \"item\": name/id or \"recipe_id\": id, \"quantity\": n }] " +
                          "(quantity = number of crafts). Editing an existing list (by id or name) replaces its recipes unless append=true. Artisan has no " +
                          "IPC for lists, so it is briefly unloaded, its config is updated (with a backup) and it is loaded again — refused while Artisan " +
                          "is crafting. Requires Artisan and 'Crafting & gathering'.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "Existing list id or name to edit; omit to create a new list." },
                    "name": { "type": "string", "description": "List name (required for a new list; renames an existing one)." },
                    "recipes": {
                      "type": "array",
                      "items": { "type": "object", "properties": { "item": { "type": "string" }, "recipe_id": { "type": "integer" }, "quantity": { "type": "integer" } } }
                    },
                    "append": { "type": "boolean", "description": "Add the recipes to the existing ones instead of replacing them (default false)." }
                  }
                }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = async (args, _) =>
            {
                RequireEnabled();
                var listArg = args.String("list");
                var name = args.String("name");
                var append = args.Bool("append", false);
                var recipes = await Game.RunLoggedIn(() =>
                {
                    RequireArtisan();
                    EnsureArtisanIdle();
                    return (args.Node("recipes") as JsonArray ?? []).OfType<JsonObject>().Select(r =>
                    {
                        var a = new ToolArgs(r);
                        var recipe = ResolveRecipe(a.String("item"), a.UInt("recipe_id"));
                        return (recipe.RowId, Quantity: a.Int("quantity", 1, 1, 9999));
                    }).ToList();
                }).ConfigureAwait(false);
                if (listArg is null && name is null) throw new ToolException("A new list needs a 'name'.");

                var (listId, backup) = await WriteCraftingList(listArg, name, recipes.Select(r => (r.RowId, r.Quantity)).ToList(), append).ConfigureAwait(false);

                return new
                {
                    list = new { id = listId, name },
                    recipes = recipes.Select(r => DescribeRecipe(r.RowId, r.Quantity)).ToList(),
                    mode = listArg is null ? "created" : append ? "appended" : "replaced",
                    backup,
                    note = "Artisan was reloaded with the new list.",
                };
            },
        };

        yield return new McpTool
        {
            Name = "delete_crafting_list",
            Description = "Deletes an Artisan crafting list (by id or name), with a backup of Artisan's config. Artisan is briefly reloaded. Requires " +
                          "Artisan and 'Crafting & gathering'.",
            InputSchema = """
                { "type": "object", "properties": { "list": { "type": "string", "description": "List id or name." } }, "required": ["list"] }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = async (args, _) =>
            {
                RequireEnabled();
                var listArg = args.String("list") ?? throw new ToolException("'list' is required.");
                await Game.RunLoggedIn(() => { RequireArtisan(); EnsureArtisanIdle(); return true; }).ConfigureAwait(false);
                string? deleted = null;
                var backup = await PluginTools.ModifyPluginJson(Artisan, null, root =>
                {
                    var lists = root["NewCraftingLists"] as JsonArray ?? throw new ToolException("Artisan's config has no crafting lists section.");
                    var list = MatchList(lists, listArg) ?? throw new ToolException($"No Artisan list '{listArg}'.");
                    deleted = list["Name"]?.ToString();
                    lists.Remove(list);
                    return root;
                }).ConfigureAwait(false);
                return new { deleted, backup };
            },
        };

        // ================================================================== GatherBuddy Reborn

        yield return new McpTool
        {
            Name = "get_gather_lists",
            Description = "GatherBuddy Reborn's auto-gather lists (name, folder, active, items with quantities) and the auto-gather state " +
                          "(enabled, waiting, status text). Requires GatherBuddy Reborn.",
            Handler = (_, _) => Game.Run<object?>(() =>
            {
                RequireGatherBuddy();
                var lists = PluginTools.ReadPluginJson(GatherBuddy, GatherListsFile) as JsonArray ?? [];
                return new
                {
                    state = GatherState(),
                    lists = lists.OfType<JsonObject>().Select((l, i) => new
                    {
                        index = i,
                        name = l["Name"]?.ToString(),
                        folder = l["FolderPath"]?.ToString() is { Length: > 0 } f ? f : null,
                        active = l["Enabled"]?.GetValue<bool>() == true,
                        fallback = l["Fallback"]?.GetValue<bool>() == true ? true : (bool?)null,
                        items = (l["ItemIds"] as JsonArray ?? []).Select(n => n!.GetValue<uint>()).Select(id => new
                        {
                            itemId = id,
                            name = InventoryTools.ItemName(id),
                            quantity = l["Quantities"]?[id.ToString()]?.GetValue<uint>(),
                            enabled = l["EnabledItems"]?[id.ToString()]?.GetValue<bool>() != false,
                        }).ToList(),
                    }).ToList(),
                };
            }),
        };

        yield return new McpTool
        {
            Name = "set_gather_list",
            Description = "Creates or edits a GatherBuddy Reborn auto-gather list: name, items as [{ \"item\": name/id, \"quantity\": n, \"enabled\": true }], " +
                          "active (whether auto-gather uses it), folder, description. Editing an existing list (by name or index) replaces its items unless " +
                          "append=true. GatherBuddy has no IPC for lists, so it is briefly unloaded, its list file updated (with a backup) and loaded again " +
                          "— refused while auto-gather runs. Requires GatherBuddy Reborn and 'Crafting & gathering'.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "list": { "type": "string", "description": "Existing list name or index to edit; omit to create a new list." },
                    "name": { "type": "string" },
                    "items": {
                      "type": "array",
                      "items": { "type": "object", "properties": { "item": { "type": "string" }, "quantity": { "type": "integer" }, "enabled": { "type": "boolean" } } }
                    },
                    "active": { "type": "boolean", "description": "Use this list for auto-gather." },
                    "folder": { "type": "string" },
                    "description": { "type": "string" },
                    "append": { "type": "boolean", "description": "Add/update items instead of replacing all (default false)." }
                  }
                }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = async (args, _) =>
            {
                RequireEnabled();
                var listArg = args.String("list");
                var name = args.String("name");
                var append = args.Bool("append", false);
                if (listArg is null && name is null) throw new ToolException("A new list needs a 'name'.");
                var items = await Game.RunLoggedIn(() =>
                {
                    RequireGatherBuddy();
                    EnsureGatherIdle();
                    return (args.Node("items") as JsonArray ?? []).OfType<JsonObject>().Select(r =>
                    {
                        var a = new ToolArgs(r);
                        return (Id: ResolveGatherable(a.String("item") ?? throw new ToolException("Each item needs 'item'.")),
                                Quantity: (uint)a.Int("quantity", 1, 1, 999999), Enabled: a.Bool("enabled", true));
                    }).ToList();
                }).ConfigureAwait(false);

                string? listName = null;
                var backup = await PluginTools.ModifyPluginJson(GatherBuddy, GatherListsFile, root =>
                {
                    var lists = root as JsonArray ?? throw new ToolException("GatherBuddy's list file has an unexpected format.");
                    JsonObject list;
                    if (listArg is not null)
                    {
                        list = MatchGatherList(lists, listArg) ?? throw new ToolException($"No GatherBuddy list '{listArg}'.");
                        if (!append) { list["ItemIds"] = new JsonArray(); list["Quantities"] = new JsonObject(); list["EnabledItems"] = new JsonObject(); }
                    }
                    else
                    {
                        list = new JsonObject
                        {
                            ["ItemIds"] = new JsonArray(), ["Quantities"] = new JsonObject(), ["PrefferedLocations"] = new JsonObject(),
                            ["EnabledItems"] = new JsonObject(), ["Name"] = name, ["Description"] = "", ["FolderPath"] = "",
                            ["Order"] = lists.Count, ["Enabled"] = false, ["Fallback"] = false, ["RemoveCompletedItems"] = false,
                        };
                        lists.Add(list);
                    }
                    if (name is not null) list["Name"] = name;
                    if (args.Node("active") is not null) list["Enabled"] = args.Bool("active", false);
                    if (args.String("folder") is { } folder) list["FolderPath"] = folder;
                    if (args.String("description") is { } description) list["Description"] = description;
                    var ids = (JsonArray)list["ItemIds"]!;
                    foreach (var (id, quantity, enabled) in items)
                    {
                        if (!ids.Any(n => n!.GetValue<uint>() == id)) ids.Add(id);
                        list["Quantities"]![id.ToString()] = quantity;
                        list["EnabledItems"]![id.ToString()] = enabled;
                    }
                    listName = list["Name"]?.ToString();
                    return root;
                }).ConfigureAwait(false);

                return new
                {
                    list = listName,
                    mode = listArg is null ? "created" : append ? "updated" : "replaced",
                    items = items.Select(i => new { itemId = i.Id, name = InventoryTools.ItemName(i.Id), quantity = i.Quantity, enabled = i.Enabled }).ToList(),
                    backup,
                    note = "GatherBuddy Reborn was reloaded with the new list.",
                };
            },
        };

        yield return new McpTool
        {
            Name = "delete_gather_list",
            Description = "Deletes a GatherBuddy Reborn auto-gather list (by name or index), with a backup. GatherBuddy is briefly reloaded. Requires " +
                          "GatherBuddy Reborn and 'Crafting & gathering'.",
            InputSchema = """
                { "type": "object", "properties": { "list": { "type": "string", "description": "List name or index." } }, "required": ["list"] }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = async (args, _) =>
            {
                RequireEnabled();
                var listArg = args.String("list") ?? throw new ToolException("'list' is required.");
                await Game.RunLoggedIn(() => { RequireGatherBuddy(); EnsureGatherIdle(); return true; }).ConfigureAwait(false);
                string? deleted = null;
                var backup = await PluginTools.ModifyPluginJson(GatherBuddy, GatherListsFile, root =>
                {
                    var lists = root as JsonArray ?? throw new ToolException("GatherBuddy's list file has an unexpected format.");
                    var list = MatchGatherList(lists, listArg) ?? throw new ToolException($"No GatherBuddy list '{listArg}'.");
                    deleted = list["Name"]?.ToString();
                    lists.Remove(list);
                    return root;
                }).ConfigureAwait(false);
                return new { deleted, backup };
            },
        };

        yield return new McpTool
        {
            Name = "set_auto_gather",
            Description = "Starts or stops GatherBuddy Reborn's auto-gather (it gathers what the active auto-gather lists need). Requires GatherBuddy Reborn " +
                          "and 'Crafting & gathering'.",
            InputSchema = """
                { "type": "object", "properties": { "enabled": { "type": "boolean" } }, "required": ["enabled"] }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                RequireEnabled();
                var enabled = args.Bool("enabled", false);
                return Game.RunLoggedIn<object?>(() =>
                {
                    RequireGatherBuddy();
                    Svc.PluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled").InvokeAction(enabled);
                    return new { autoGather = enabled, state = GatherState() };
                });
            },
        };
    }

    // ------------------------------------------------------------------ Artisan helpers

    internal static void RequireArtisan()
    {
        if (!ArtisanLoaded) throw new ToolException("Artisan is not installed or not enabled.");
    }

    internal static object ArtisanState() => new
    {
        listRunning = Ipc("Artisan.IsListRunning"),
        listPaused = Ipc("Artisan.IsListPaused"),
        endurance = Ipc("Artisan.GetEnduranceStatus"),
        busy = Ipc("Artisan.IsBusy"),
    };

    internal static void EnsureArtisanIdle()
    {
        if (Ipc("Artisan.IsBusy") == true || Ipc("Artisan.IsListRunning") == true)
            throw new ToolException("Artisan is busy (crafting or running a list). Stop or pause it first.");
    }

    /// <summary>
    /// Creates a list (listArg null) or edits one, with recipes in the given order (Artisan crafts in list order), by editing Artisan's
    /// config with Artisan briefly unloaded. Returns the list id and the backup path.
    /// </summary>
    internal static async Task<(int Id, string? Backup)> WriteCraftingList(string? listArg, string? name, List<(uint RecipeId, int Quantity)> recipes, bool append)
    {
        int listId = 0;
        var backup = await PluginTools.ModifyPluginJson(Artisan, null, root =>
        {
            var lists = root["NewCraftingLists"] as JsonArray ?? throw new ToolException("Artisan's config has no crafting lists section.");
            JsonObject list;
            if (listArg is not null)
            {
                list = MatchList(lists, listArg) ?? throw new ToolException($"No Artisan list '{listArg}'.");
                if (!append) list["Recipes"] = new JsonArray();
            }
            else
            {
                var used = lists.OfType<JsonObject>().Select(l => l["ID"]?.GetValue<int>() ?? 0).ToHashSet();
                int id;
                do id = Random.Shared.Next(10000, 99999); while (used.Contains(id));
                list = new JsonObject
                {
                    ["$type"] = "Artisan.CraftingLists.NewCraftingList, Artisan",
                    ["ID"] = id,
                    ["Name"] = name,
                    ["Recipes"] = new JsonArray(),
                    ["ExpandedList"] = new JsonArray(),
                };
                lists.Add(list);
            }
            if (name is not null) list["Name"] = name;
            var target = (JsonArray)list["Recipes"]!;
            foreach (var (recipeId, quantity) in recipes)
            {
                var existing = target.OfType<JsonObject>().FirstOrDefault(r => r["ID"]?.GetValue<uint>() == recipeId);
                if (existing is not null) existing["Quantity"] = quantity;
                else target.Add(new JsonObject
                {
                    ["$type"] = "Artisan.CraftingLists.ListItem, Artisan",
                    ["ID"] = recipeId,
                    ["Quantity"] = quantity,
                    ["ListItemOptions"] = new JsonObject { ["$type"] = "Artisan.CraftingLists.ListItemOptions, Artisan", ["NQOnly"] = false, ["Skipping"] = false },
                });
            }
            listId = list["ID"]!.GetValue<int>();
            return root;
        }).ConfigureAwait(false);
        return (listId, backup);
    }

    private sealed record ListRef(int Id, string? Name);

    private static ListRef FindList(string listArg)
    {
        var lists = PluginTools.ReadPluginJson(Artisan, null)["NewCraftingLists"] as JsonArray ?? [];
        var list = MatchList(lists, listArg) ?? throw new ToolException($"No Artisan list '{listArg}'. Use get_crafting_lists.");
        return new ListRef(list["ID"]!.GetValue<int>(), list["Name"]?.ToString());
    }

    internal static JsonObject? MatchList(JsonArray lists, string listArg) =>
        lists.OfType<JsonObject>().FirstOrDefault(l => int.TryParse(listArg, out var id) && l["ID"]?.GetValue<int>() == id)
        ?? lists.OfType<JsonObject>().FirstOrDefault(l => string.Equals(l["Name"]?.ToString(), listArg, StringComparison.OrdinalIgnoreCase))
        ?? (lists.OfType<JsonObject>().Where(l => Game.Matches(l["Name"]?.ToString(), listArg)).ToList() is { Count: 1 } one ? one[0] : null);

    /// <summary>A recipe by id, or by item (name or id) — preferring the current job's recipe when several jobs can craft it.</summary>
    private static Recipe ResolveRecipe(string? item, uint? recipeId)
    {
        var recipes = Svc.Data.GetExcelSheet<Recipe>();
        if (recipeId is { } rid)
            return recipes.GetRowOrDefault(rid) ?? throw new ToolException($"No recipe {rid}.");
        if (item is null) throw new ToolException("Give 'item' or 'recipe_id'.");

        var items = Svc.Data.GetExcelSheet<Item>();
        var itemIds = uint.TryParse(item, out var iid)
            ? [iid]
            : items.Where(i => i.Name.ExtractText().Equals(item, StringComparison.OrdinalIgnoreCase)).Select(i => i.RowId).ToList() is { Count: > 0 } exact
                ? exact
                : items.Where(i => !i.Name.IsEmpty && Game.Matches(i.Name.ExtractText(), item)).Select(i => i.RowId).Take(50).ToList();
        var candidates = recipes.Where(r => r.ItemResult.RowId != 0 && itemIds.Contains(r.ItemResult.RowId)).ToList();
        if (candidates.Count == 0) throw new ToolException($"No craftable item matches '{item}'.");
        if (candidates.Select(c => c.ItemResult.RowId).Distinct().Count() > 1)
            throw new ToolException($"'{item}' matches several items: {string.Join(", ", candidates.Select(c => Excel.Name(c.ItemResult)).Distinct().Take(8))}. Be more specific.");

        // Recipe.CraftType 0-7 = CRP..CUL = ClassJob 8..15.
        var currentJob = Svc.PlayerState.ClassJob.RowId;
        return candidates.FirstOrDefault(c => c.CraftType.RowId + 8 == currentJob) is { RowId: not 0 } mine ? mine : candidates[0];
    }

    internal static object DescribeRecipe(uint recipeId, int quantity)
    {
        var r = Svc.Data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId);
        var job = r is { } rr ? Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(rr.CraftType.RowId + 8) : null;
        return new
        {
            recipeId,
            item = r is { } x ? Excel.Name(x.ItemResult) : null,
            job = job is { } j ? j.Abbreviation.ExtractText() : null,
            level = r?.RecipeLevelTable.ValueNullable?.ClassJobLevel,
            quantity,
        };
    }

    // ------------------------------------------------------------------ GatherBuddy helpers

    internal static void RequireGatherBuddy()
    {
        if (!GatherBuddyLoaded) throw new ToolException("GatherBuddy Reborn is not installed or not enabled.");
    }

    internal static object GatherState() => new
    {
        autoGather = Ipc("GatherBuddyReborn.IsAutoGatherEnabled"),
        waiting = Ipc("GatherBuddyReborn.IsAutoGatherWaiting"),
        status = SafeString("GatherBuddyReborn.GetAutoGatherStatusText"),
    };

    private static void EnsureGatherIdle()
    {
        if (Ipc("GatherBuddyReborn.IsAutoGatherEnabled") == true)
            throw new ToolException("GatherBuddy's auto-gather is running; stop it first (set_auto_gather enabled=false).");
    }

    internal static JsonObject? MatchGatherList(JsonArray lists, string listArg) =>
        (int.TryParse(listArg, out var index) && index >= 0 && index < lists.Count ? lists[index] as JsonObject : null)
        ?? lists.OfType<JsonObject>().FirstOrDefault(l => string.Equals(l["Name"]?.ToString(), listArg, StringComparison.OrdinalIgnoreCase))
        ?? (lists.OfType<JsonObject>().Where(l => Game.Matches(l["Name"]?.ToString(), listArg)).ToList() is { Count: 1 } one ? one[0] : null);

    /// <summary>Item id of a gatherable (or fish) by id or name — GatherBuddy's own name matching first, then the item sheet.</summary>
    internal static uint ResolveGatherable(string item)
    {
        if (uint.TryParse(item, out var id)) return id;
        try
        {
            var identified = Svc.PluginInterface.GetIpcSubscriber<string, uint>("GatherBuddyReborn.Identify").InvokeFunc(item);
            if (identified != 0) return identified;
        }
        catch { /* fall back to the item sheet */ }
        var match = Svc.Data.GetExcelSheet<Item>().FirstOrDefault(i => i.Name.ExtractText().Equals(item, StringComparison.OrdinalIgnoreCase));
        return match.RowId != 0 ? match.RowId : throw new ToolException($"GatherBuddy doesn't know a gatherable called '{item}'.");
    }

    // ------------------------------------------------------------------ IPC helpers

    internal static bool? Ipc(string name)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<bool>(name).InvokeFunc(); }
        catch { return null; }
    }

    internal static string? SafeString(string name)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<string>(name).InvokeFunc(); }
        catch { return null; }
    }
}
