using System.Text.Json;
using XivMcp.Crafting;

namespace XivMcp.Tests;

public class CraftListResumeTests
{
    private static readonly List<RecipeCount> List = [new(100, 6), new(200, 3), new(300, 1)];

    [Fact]
    public void A_first_run_uses_the_list_as_it_is()
    {
        var plan = CraftListResume.Plan(null, 7, List);
        Assert.Equal(List, plan.Original);
        Assert.Empty(plan.Done);
        Assert.Null(plan.Shrink);
        Assert.False(plan.AllDone);
    }

    [Fact]
    public void A_resumed_run_shrinks_the_list_to_what_is_left()
    {
        var memory = new CraftListMemory(7, List, List, new() { [100] = 6, [200] = 1 });
        var plan = CraftListResume.Plan(memory, 7, List);
        Assert.Equal(List, plan.Original);
        Assert.Equal(6, plan.Done[100]);
        Assert.Equal([new RecipeCount(200, 2), new RecipeCount(300, 1)], plan.Shrink);
        Assert.False(plan.AllDone);
    }

    [Fact]
    public void A_list_that_was_already_shrunk_is_not_written_again()
    {
        List<RecipeCount> left = [new(200, 2), new(300, 1)];
        var memory = new CraftListMemory(7, List, left, new() { [100] = 6, [200] = 1 });
        var plan = CraftListResume.Plan(memory, 7, left);
        Assert.Null(plan.Shrink);
        Assert.Equal(List, plan.Original);
    }

    [Fact]
    public void Progress_made_after_the_shrink_shrinks_it_further()
    {
        List<RecipeCount> left = [new(200, 2), new(300, 1)];
        var memory = new CraftListMemory(7, List, left, new() { [100] = 6, [200] = 3 });
        var plan = CraftListResume.Plan(memory, 7, left);
        Assert.Equal([new RecipeCount(300, 1)], plan.Shrink);
    }

    [Fact]
    public void Everything_done_needs_no_run()
    {
        var memory = new CraftListMemory(7, List, List, new() { [100] = 6, [200] = 3, [300] = 1 });
        var plan = CraftListResume.Plan(memory, 7, List);
        Assert.True(plan.AllDone);
    }

    [Fact]
    public void A_changed_or_different_list_starts_fresh()
    {
        var memory = new CraftListMemory(7, List, List, new() { [100] = 6 });
        List<RecipeCount> edited = [new(100, 6), new(200, 5)];
        Assert.Null(CraftListResume.Plan(memory, 7, edited).Shrink);
        Assert.Empty(CraftListResume.Plan(memory, 7, edited).Done);
        Assert.Empty(CraftListResume.Plan(memory, 8, List).Done);
    }

    [Fact]
    public void Memory_survives_a_round_trip_through_json()
    {
        var memory = new CraftListMemory(7, List, [new(200, 2)], new() { [100] = 6, [200] = 1 });
        var copy = CraftListMemory.FromJson(JsonSerializer.SerializeToNode(memory.ToJson())!.AsObject());
        Assert.NotNull(copy);
        Assert.Equal(memory.ListId, copy.ListId);
        Assert.Equal(memory.Original, copy.Original);
        Assert.Equal(memory.Written, copy.Written);
        Assert.Equal(memory.Done, copy.Done);
        Assert.Null(CraftListMemory.FromJson(null));
        Assert.Null(CraftListMemory.FromJson(new System.Text.Json.Nodes.JsonObject { ["x"] = 1 }));
    }
}
