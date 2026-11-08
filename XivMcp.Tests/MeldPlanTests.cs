using System;
using System.Linq;
using XivMcp.Gear;

namespace XivMcp.Tests;

public class MeldPlanTests
{
    // Grades are 0-based (Materia I = 0). Overmeld chances like the game's table: lower grades meld more easily; grade 5 only in the first overmeld slot.
    private static int Chance(int grade, int overmeld, bool gatherCraft) => (grade, overmeld) switch
    {
        (5, 0) => 12,
        (5, _) => 0,
        (_, 0) => 50 - grade * 5,
        (_, 1) => 30 - grade * 5,
        (_, 2) => 20 - grade * 3,
        _ => 10 - grade,
    };

    private static MateriaPick M(int grade, string name = "") => new(1000u + (uint)grade, name == "" ? $"Materia {grade + 1}" : name, grade, true);

    private static MeldItem Item(int slots = 2, bool advanced = true, int filled = 0) => new("Ring", slots, advanced, filled);

    [Fact]
    public void Guaranteed_slots_meld_for_sure()
    {
        var plan = MeldPlanner.Plan(Item(), [M(0)], Chance, advancedUnlocked: false);
        var step = Assert.Single(plan);
        Assert.Equal(0, step.Slot);
        Assert.Equal(100, step.ChancePercent);
    }

    [Fact]
    public void Slots_already_filled_come_first()
    {
        var plan = MeldPlanner.Plan(Item(filled: 1), [M(0)], Chance, false);
        Assert.Equal(1, Assert.Single(plan).Slot);
    }

    [Fact]
    public void Overmelds_take_the_game_chance_of_their_slot()
    {
        var plan = MeldPlanner.Plan(Item(slots: 1), [M(2), M(2), M(2)], Chance, true);
        Assert.Equal([100, 40, 20], plan.Select(s => s.ChancePercent));
        Assert.Equal([0, 1, 2], plan.Select(s => s.Slot));
    }

    [Fact]
    public void Harder_materia_goes_into_the_safer_slots()
    {
        // Grade 5 can only be overmelded into the first overmeld slot, and is cheaper in a guaranteed one: it goes first.
        var plan = MeldPlanner.Plan(Item(slots: 1), [M(1), M(5)], Chance, true);
        Assert.Equal([5, 1], plan.Select(s => s.Materia.Grade));
        Assert.Equal([100, 45], plan.Select(s => s.ChancePercent));
    }

    [Fact]
    public void Materia_the_game_forbids_in_a_slot_is_refused()
    {
        var e = Assert.Throws<MeldPlanException>(() => MeldPlanner.Plan(Item(slots: 1), [M(5), M(5), M(5)], Chance, true));
        Assert.Contains("overmeld slot 2", e.Message);
    }

    [Fact]
    public void Overmelding_needs_the_item_to_allow_it()
    {
        var e = Assert.Throws<MeldPlanException>(() => MeldPlanner.Plan(Item(slots: 2, advanced: false), [M(0), M(0), M(0)], Chance, true));
        Assert.Contains("only 2", e.Message);
    }

    [Fact]
    public void Overmelding_needs_advanced_melding_unlocked()
    {
        var e = Assert.Throws<MeldPlanException>(() => MeldPlanner.Plan(Item(slots: 1), [M(0), M(0)], Chance, advancedUnlocked: false));
        Assert.Contains("Advanced Materia Melding", e.Message);
    }

    [Fact]
    public void No_more_than_five_materia()
    {
        var e = Assert.Throws<MeldPlanException>(() => MeldPlanner.Plan(Item(slots: 2, filled: 2), [M(0), M(0), M(0), M(0)], Chance, true));
        Assert.Contains("5", e.Message);
    }

    [Fact]
    public void Items_without_slots_take_no_materia()
    {
        var e = Assert.Throws<MeldPlanException>(() => MeldPlanner.Plan(Item(slots: 0, advanced: false), [M(0)], Chance, true));
        Assert.Contains("can't", e.Message);
    }

    [Fact]
    public void Expected_materia_use_counts_overmeld_failures()
    {
        var plan = MeldPlanner.Plan(Item(slots: 1), [M(0), M(0)], Chance, true);
        Assert.Equal(1 + 2.0, MeldPlanner.ExpectedMateria(plan), 3); // 100% + 50% (two tries on average)
    }

    [Theory]
    [InlineData(0, 5, 3, true)]
    [InlineData(5, 5, 3, false)]  // failed as often as allowed
    [InlineData(1, 5, 0, false)]  // no materia left
    public void Overmelds_are_retried_within_the_limits(int failures, int maxFailures, int left, bool again) =>
        Assert.Equal(again, MeldPlanner.TryAgain(failures, maxFailures, left));
}
