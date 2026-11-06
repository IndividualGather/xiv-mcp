using XivMcp.Crafting;

namespace XivMcp.Tests;

public class CraftListProgressTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    // Ingot (item 10, ingredient 1) → Plate (item 20, uses ingots) → Hammer (item 30, uses plates).
    private static readonly ListRecipe Ingot = new(100, "Ingot", 10, 6, 1, []);
    private static readonly ListRecipe Plate = new(200, "Plate", 20, 3, 1, []);
    private static readonly ListRecipe Hammer = new(300, "Wrapped Hammer", 30, 1, 1, ["Steel Hammer"]);

    private static Dictionary<uint, int> Counts(int ore, int ingot, int plate, int hammer) => new() { [1] = ore, [10] = ingot, [20] = plate, [30] = hammer };

    [Fact]
    public void Crafts_are_counted_from_items_added_even_when_later_crafts_use_them_up()
    {
        var p = new CraftListProgress([Ingot, Plate], Counts(12, 0, 0, 0), T0);
        p.Observe(Counts(10, 0, 0, 0), T0.AddSeconds(5));  // synthesis started: ore used
        p.Observe(Counts(10, 1, 0, 0), T0.AddSeconds(30)); // ingot done
        for (var i = 2; i <= 6; i++) p.Observe(Counts(12 - 2 * i, i, 0, 0), T0.AddSeconds(30 * i));
        p.Observe(Counts(0, 4, 0, 0), T0.AddSeconds(200)); // plate synthesis took 2 ingots
        p.Observe(Counts(0, 4, 1, 0), T0.AddSeconds(230));
        Assert.Equal(6, p.CraftsDone(Ingot.RecipeId));
        Assert.Equal(1, p.CraftsDone(Plate.RecipeId));
        Assert.Equal(Plate, p.Current);
    }

    [Fact]
    public void Multi_yield_recipes_count_crafts_not_items()
    {
        var shafts = new ListRecipe(400, "Shaft", 40, 2, 3, []);
        var p = new CraftListProgress([shafts], new Dictionary<uint, int> { [40] = 0 }, T0);
        p.Observe(new Dictionary<uint, int> { [40] = 3 }, T0.AddSeconds(30));
        Assert.Equal(1, p.CraftsDone(400));
        p.Observe(new Dictionary<uint, int> { [40] = 6 }, T0.AddSeconds(60));
        Assert.Equal(2, p.CraftsDone(400));
        Assert.Null(p.Current);
        Assert.True(p.Finished);
    }

    [Fact]
    public void Items_made_by_two_entries_fill_them_in_list_order()
    {
        var first = new ListRecipe(1, "Rivets", 50, 2, 1, []);
        var second = new ListRecipe(2, "Rivets", 50, 3, 1, []);
        var p = new CraftListProgress([first, second], new Dictionary<uint, int> { [50] = 0 }, T0);
        p.Observe(new Dictionary<uint, int> { [50] = 4 }, T0.AddSeconds(5));
        Assert.Equal(2, p.CraftsDone(1));
        Assert.Equal(2, p.CraftsDone(2));
    }

    [Fact]
    public void Earlier_progress_is_kept()
    {
        var p = new CraftListProgress([Ingot, Plate], Counts(0, 6, 0, 0), T0, new Dictionary<uint, int> { [100] = 6 });
        Assert.Equal(6, p.CraftsDone(100));
        Assert.Equal(Plate, p.Current);
        Assert.False(p.MadeProgressThisRun);
    }

    [Fact]
    public void No_count_change_for_the_stall_time_is_a_stall()
    {
        var p = new CraftListProgress([Ingot, Plate, Hammer], Counts(0, 0, 0, 0), T0);
        var stall = TimeSpan.FromMinutes(5);
        Assert.False(p.Stalled(T0.AddMinutes(4), stall));
        p.Observe(Counts(0, 1, 0, 0), T0.AddMinutes(4));
        Assert.False(p.Stalled(T0.AddMinutes(8), stall));
        p.Observe(Counts(0, 1, 0, 0), T0.AddMinutes(8)); // nothing changed
        Assert.True(p.Stalled(T0.AddMinutes(9), stall));
    }

    [Fact]
    public void Other_activity_postpones_the_stall()
    {
        var p = new CraftListProgress([Ingot], Counts(0, 0, 0, 0), T0);
        p.Touch(T0.AddMinutes(4)); // e.g. Artisan's solver is still working
        Assert.False(p.Stalled(T0.AddMinutes(8), TimeSpan.FromMinutes(5)));
        Assert.True(p.Stalled(T0.AddMinutes(9), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_zero_stall_time_never_stalls()
    {
        var p = new CraftListProgress([Ingot], Counts(0, 0, 0, 0), T0);
        Assert.False(p.Stalled(T0.AddHours(5), TimeSpan.Zero));
    }

    [Fact]
    public void Stall_message_names_the_recipe_and_the_equipment_ingredient()
    {
        var p = new CraftListProgress([Ingot, Hammer], Counts(0, 0, 0, 0), T0, new Dictionary<uint, int> { [100] = 6 });
        var message = p.StallMessage("MCP: Hammer", TimeSpan.FromMinutes(5));
        Assert.Contains("Wrapped Hammer", message);
        Assert.Contains("recipe 300", message);
        Assert.Contains("Steel Hammer", message);
        Assert.Contains("equipment", message);
        Assert.Contains("by hand", message);
    }

    [Fact]
    public void Stall_message_without_equipment_lists_the_likely_causes()
    {
        var p = new CraftListProgress([Ingot], Counts(0, 0, 0, 0), T0);
        var message = p.StallMessage("L", TimeSpan.FromMinutes(5));
        Assert.Contains("Ingot", message);
        Assert.DoesNotContain("equipment as an ingredient", message);
        Assert.Contains("missing materials", message);
    }

    [Fact]
    public void Gear_note_only_for_recipes_with_equipment_ingredients()
    {
        Assert.Null(GearIngredients.Note([]));
        var note = GearIngredients.Note(["Hard Leather Grimoire"]);
        Assert.Contains("Hard Leather Grimoire", note);
        Assert.Contains("by hand", note);
        Assert.True(GearIngredients.IsGear(equipSlotCategory: 1));
        Assert.False(GearIngredients.IsGear(equipSlotCategory: 0));
    }
}
