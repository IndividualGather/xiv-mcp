using XivMcp.Duties;

namespace XivMcp.Tests;

public class AutoDutyProfileTests
{
    private static readonly string[] Existing = ["Bare", "My Profile", "Tome Farm (Keep Gear)", "Farm Chest"];

    [Fact]
    public void The_characters_default_comes_first()
    {
        Assert.Equal("My Profile", AutoDutyProfiles.Source("My Profile", "Tome Farm (Keep Gear)", "Farm Chest", Existing));
    }

    [Fact]
    public void Without_one_the_global_default_is_copied()
    {
        Assert.Equal("Tome Farm (Keep Gear)", AutoDutyProfiles.Source(null, "Tome Farm (Keep Gear)", "Farm Chest", Existing));
    }

    [Fact]
    public void Missing_defaults_fall_back_to_the_active_profile()
    {
        Assert.Equal("Farm Chest", AutoDutyProfiles.Source("Gone", "Also gone", "Farm Chest", Existing));
    }

    [Fact]
    public void A_left_over_temporary_profile_is_never_copied()
    {
        Assert.True(AutoDutyProfiles.IsTemporary(AutoDutyProfiles.Temporary));
        Assert.Equal("Bare", AutoDutyProfiles.Source(null, null, AutoDutyProfiles.Temporary, [.. Existing, AutoDutyProfiles.Temporary]));
        Assert.Equal("Bare", AutoDutyProfiles.Source(null, AutoDutyProfiles.Temporary, AutoDutyProfiles.Temporary, [.. Existing, AutoDutyProfiles.Temporary]));
    }
}

public class DutyChoiceTests
{
    private static DutyOption D(string name, int level, bool path, params string[] modes) => new(name, (uint)level, level, path, modes);

    [Fact]
    public void Duties_without_a_path_are_skipped()
    {
        Assert.Null(DutyChoice.Best([D("Sastasha", 15, false, "Support")]));
    }

    [Fact]
    public void Running_with_npcs_beats_a_lower_level()
    {
        var best = DutyChoice.Best([D("Party only", 15, true, "Regular"), D("With NPCs", 50, true, "Regular", "Support")])!.Value;
        Assert.Equal(("With NPCs", "Support"), (best.Duty.Name, best.Mode));
    }

    [Fact]
    public void Among_equals_the_lowest_level_wins()
    {
        var best = DutyChoice.Best([D("High", 90, true, "Trust"), D("Low", 51, true, "Trust")])!.Value;
        Assert.Equal(("Low", "Trust"), (best.Duty.Name, best.Mode));
    }

    [Fact]
    public void Support_comes_before_trust()
    {
        var best = DutyChoice.Best([D("Trust only", 15, true, "Trust"), D("Support", 50, true, "Trust", "Support")])!.Value;
        Assert.Equal("Support", best.Mode);
    }
}
