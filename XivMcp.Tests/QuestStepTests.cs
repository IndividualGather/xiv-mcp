using XivMcp.Quests;

namespace XivMcp.Tests;

public class QuestStepTests
{
    [Fact]
    public void Sequence255IsTheFinalStep()
    {
        Assert.True(QuestStep.IsFinal(255));
        Assert.False(QuestStep.IsFinal(0));
        Assert.False(QuestStep.IsFinal(3));
    }

    [Fact]
    public void TheFinalStepNoteDoesNotClaimTheItemsAreReady()
    {
        // A crafter class quest at its final step still needs the items it asks for; the step alone doesn't say they are in the bags.
        Assert.Contains("not", QuestStep.FinalStepMeaning);
        Assert.Contains("items", QuestStep.FinalStepMeaning);
        Assert.DoesNotContain("ready", QuestStep.FinalStepMeaning, System.StringComparison.OrdinalIgnoreCase);
    }
}
