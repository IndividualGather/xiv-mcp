using XivMcp.Gear;

namespace XivMcp.Tests;

public class GearsetEditTests
{
    private static readonly GearsetEdit.Target Goldsmith32 = new(32, "Goldsmith", JobId: 11, Job: "GSM", Current: false);

    [Fact]
    public void Saving_needs_the_sets_job()
    {
        var problem = GearsetEdit.Problem(GearsetEdit.Action.Save, Goldsmith32, currentJob: 10, currentJobAbbreviation: "ARM", newName: null);
        Assert.Equal("Gearset 32 (Goldsmith) is a GSM set, but you are ARM: wear a GSM set first (with 'from', or switch_gearset).", problem);
    }

    [Fact]
    public void Saving_as_the_same_job_is_fine()
    {
        Assert.Null(GearsetEdit.Problem(GearsetEdit.Action.Save, Goldsmith32, currentJob: 11, currentJobAbbreviation: "GSM", newName: null));
    }

    [Fact]
    public void The_worn_set_cant_be_deleted()
    {
        var problem = GearsetEdit.Problem(GearsetEdit.Action.Delete, Goldsmith32 with { Current = true }, 11, "GSM", null);
        Assert.Equal("Gearset 32 (Goldsmith) is the one you are wearing: switch to another set before deleting it.", problem);
    }

    [Fact]
    public void Another_set_can_be_deleted()
    {
        Assert.Null(GearsetEdit.Problem(GearsetEdit.Action.Delete, Goldsmith32, 11, "GSM", null));
    }

    [Theory]
    [InlineData(null, "Give the new name in 'name'.")]
    [InlineData("  ", "Give the new name in 'name'.")]
    public void Renaming_needs_a_name(string? name, string expected)
    {
        Assert.Equal(expected, GearsetEdit.Problem(GearsetEdit.Action.Rename, Goldsmith32, 11, "GSM", name));
    }

    [Fact]
    public void Names_longer_than_the_game_allows_are_refused()
    {
        var problem = GearsetEdit.Problem(GearsetEdit.Action.Rename, Goldsmith32, 11, "GSM", new string('x', 48));
        Assert.Equal("Gearset names can be at most 47 bytes long.", problem);
    }

    [Fact]
    public void A_good_name_is_fine()
    {
        Assert.Null(GearsetEdit.Problem(GearsetEdit.Action.Rename, Goldsmith32, 11, "GSM", "Goldsmith (old)"));
    }
}
