using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Reflection access to Artisan's Raphael solver integration, which has no IPC. Artisan keys every Raphael solution by the exact
/// crafting stats (gearset + food + potion), the recipe and the solver options, and only builds one when a craft starts (aborting
/// the craft if the solver times out). This bridge does what Artisan's own crafting-list UI does to predict stats
/// (GetBaseStatsForClassHeuristic + AddConsumables + BuildCraftStateForRecipe) and asks Artisan's RaphaelCache to build the solution
/// ahead of time, so the cached solution is exactly the one Artisan looks up when the craft starts.
/// May need an update when Artisan changes these internals.
/// </summary>
internal static class ArtisanBridge
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private const string RaphaelSolverType = "Artisan.CraftingLogic.Solvers.RaphaelSolverDefintion";
    private const int RaphaelFlavour = 3;

    private sealed record Api(Assembly Asm, object Plugin)
    {
        public Type T(string name) => Asm.GetType(name) ?? throw new ToolException($"Artisan type {name} not found; this Artisan version is not supported for Raphael preparation.");
        public object Config => Field(Plugin, "Config") ?? throw new ToolException("Artisan's configuration is not available.");
    }

    private static Api Resolve()
    {
        var local = DalamudInternals.Find("Artisan");
        if (!DalamudInternals.IsLoaded(local)) throw new ToolException("Artisan is not loaded.");
        var instance = local.GetType().GetField("instance", All)?.GetValue(local)
                       ?? throw new ToolException("Could not reach Artisan's plugin instance (Dalamud internals changed?).");
        return new Api(instance.GetType().Assembly, instance);
    }

    public static bool CliAvailable()
    {
        var api = Resolve();
        return Invoke(api.T("Artisan.CraftingLogic.Solvers.RaphaelCache"), "CLIExists") is true;
    }

    /// <summary>Raphael settings that matter for planning (timeout per solve, threads).</summary>
    public static object Settings()
    {
        var api = Resolve();
        var raph = Field(api.Config, "RaphaelSolverConfig");
        return new
        {
            timeoutMinutes = raph is null ? null : Field(raph, "TimeOutMins"),
            maximumThreads = raph is null ? null : Field(raph, "MaximumThreads"),
            autoGenerate = raph is null ? null : Field(raph, "AutoGenerate"),
            cliInstalled = Invoke(api.T("Artisan.CraftingLogic.Solvers.RaphaelCache"), "CLIExists"),
        };
    }

    public sealed record Prediction(uint RecipeId, int Craftsmanship, int Control, int Cp, int Level, bool Specialist, bool Expert,
                                    bool HasSolution, bool InProgress, bool RaphaelSelected, int? Steps, string Key, uint Food, uint Potion);

    /// <summary>The craft Artisan expects for a recipe (stats it will craft with) and whether a Raphael solution is cached for it. Framework thread.</summary>
    public static Prediction Predict(uint recipeId)
    {
        var api = Resolve();
        var (craft, raphConfig, food, potion) = BuildCraft(api, recipeId);
        var cache = api.T("Artisan.CraftingLogic.Solvers.RaphaelCache");
        var args = new object?[] { craft, raphConfig, null };
        var has = (bool)cache.GetMethods(All).First(m => m.Name == "HasSolution" && m.GetParameters().Length == 3).Invoke(null, args)!;
        var macro = args[2];
        var steps = macro is null ? null : (Field(macro, "Steps") as ICollection)?.Count;
        var inProgress = (bool)Invoke(cache, "InProgress", craft, raphConfig)!;
        var key = (string)Invoke(cache, "GetTextKey", craft, raphConfig)!;
        return new Prediction(recipeId, (int)Field(craft, "StatCraftsmanship")!, (int)Field(craft, "StatControl")!, (int)Field(craft, "StatCP")!,
            (int)Field(craft, "StatLevel")!, (bool)Field(craft, "Specialist")!, (bool)Field(craft, "CraftExpert")!, has, inProgress,
            IsRaphaelSelected(api, recipeId), steps, key, food, potion);
    }

    /// <summary>Starts building the Raphael solution for a recipe (no-op if one is cached or being built). Framework thread.</summary>
    public static void Build(uint recipeId)
    {
        var api = Resolve();
        var (craft, raphConfig, _, _) = BuildCraft(api, recipeId);
        var cache = api.T("Artisan.CraftingLogic.Solvers.RaphaelCache");
        if ((int)Field(craft, "StatLevel")! < 7) throw new ToolException("Raphael needs Master's Mend (crafter level 7+) on that job.");
        var buildArgs = new object?[] { craft, raphConfig, false };
        cache.GetMethods(All).First(m => m.Name == "Build" && m.GetParameters().Length == 3).Invoke(null, buildArgs);
    }

    /// <summary>Any Raphael build running in Artisan right now.</summary>
    public static bool AnyInProgress()
    {
        var api = Resolve();
        return Invoke(api.T("Artisan.CraftingLogic.Solvers.RaphaelCache"), "InProgressAny") is true;
    }

    /// <summary>Selects Artisan's Raphael solver for a recipe (Artisan's per-recipe solver setting) and saves Artisan's config. Framework thread.</summary>
    public static bool SelectRaphael(uint recipeId)
    {
        var api = Resolve();
        if (IsRaphaelSelected(api, recipeId)) return false;
        var configs = (IDictionary)Field(api.Config, "RecipeConfigs")!;
        var recipeConfigType = api.T("Artisan.CraftingLogic.RecipeConfig");
        var rc = configs.Contains(recipeId) ? configs[recipeId]! : Activator.CreateInstance(recipeConfigType)!;
        SetField(rc, "SolverType", RaphaelSolverType);
        SetField(rc, "SolverFlavour", RaphaelFlavour);
        configs[recipeId] = rc;
        return true;
    }

    /// <summary>Saves Artisan's configuration (this also writes its Raphael cache file).</summary>
    public static void Save()
    {
        var api = Resolve();
        api.Config.GetType().GetMethod("Save", All, Type.EmptyTypes)!.Invoke(api.Config, null);
    }

    private static bool IsRaphaelSelected(Api api, uint recipeId)
    {
        var configs = (IDictionary)Field(api.Config, "RecipeConfigs")!;
        if (!configs.Contains(recipeId)) return false;
        return (Field(configs[recipeId]!, "SolverType") as string)?.Contains("Raphael", StringComparison.Ordinal) == true;
    }

    /// <summary>The CraftState Artisan's crafting-list UI predicts for a recipe, and the Raphael options it would use.</summary>
    private static (object Craft, object RaphConfig, uint Food, uint Potion) BuildCraft(Api api, uint recipeId)
    {
        var recipe = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>().GetRowOrDefault(recipeId) ?? throw new ToolException($"No recipe {recipeId}.");
        var statsType = api.T("Artisan.GameInterop.CharacterStats");
        var consumableType = api.T("Artisan.GameInterop.ConsumableStats");
        var crafting = api.T("Artisan.GameInterop.Crafting");
        var cache = api.T("Artisan.CraftingLogic.Solvers.RaphaelCache");

        var heuristic = statsType.GetMethod("GetBaseStatsForClassHeuristic", All)!;
        var jobType = heuristic.GetParameters()[0].ParameterType;
        var job = Enum.ToObject(jobType, 8 + (int)recipe.CraftType.RowId);
        var stats = heuristic.Invoke(null, [job])!;

        // Consumables configured for this recipe in Artisan (or its defaults).
        var configs = (IDictionary)Field(api.Config, "RecipeConfigs")!;
        var rc = configs.Contains(recipeId) ? configs[recipeId]! : Activator.CreateInstance(api.T("Artisan.CraftingLogic.RecipeConfig"))!;
        var food = (uint)Prop(rc, "RequiredFood")!;
        var potion = (uint)Prop(rc, "RequiredPotion")!;
        var foodStats = Activator.CreateInstance(consumableType, food, (bool)Prop(rc, "RequiredFoodHQ")!)!;
        var potionStats = Activator.CreateInstance(consumableType, potion, (bool)Prop(rc, "RequiredPotionHQ")!)!;
        var fcBuff = api.T("Artisan.RawInformation.Character.CharacterInfo").GetField("FCCraftsmanshipbuff", All)?.GetValue(null);
        statsType.GetMethod("AddConsumables", All)!.Invoke(stats, [foodStats, potionStats, fcBuff]); // boxed struct is updated in place

        var craft = crafting.GetMethod("BuildCraftStateForRecipe", All)!.Invoke(null, [stats, job, recipe])!;
        var raphConfig = cache.GetMethod("GetRaphConfig", All)!.Invoke(null, [craft, true])!;
        return (craft, raphConfig, food is 0 or 1 ? 0 : food, potion is 0 or 1 ? 0 : potion);
    }

    // ------------------------------------------------------------------ reflection helpers

    private static object? Invoke(Type type, string method, params object?[] args) =>
        type.GetMethods(All).First(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(null, args);

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, All)?.GetValue(target) ?? target.GetType().GetProperty(name, All)?.GetValue(target);

    private static object? Prop(object target, string name) =>
        target.GetType().GetProperty(name, All)?.GetValue(target) ?? target.GetType().GetField(name, All)?.GetValue(target);

    private static void SetField(object target, string name, object value) =>
        (target.GetType().GetField(name, All) ?? throw new ToolException($"Artisan field {name} not found.")).SetValue(target, value);
}
