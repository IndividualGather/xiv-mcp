using System;
using XivMcp.Ui;

namespace XivMcp.Tests;

public class BellOpeningTests
{
    private static BellState State(bool list = false, bool talk = false, bool reacted = false, double elapsed = 1, double sinceInteract = 1,
                                   double sinceAction = 5, int interactions = 1) =>
        new(list, talk, reacted, TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(sinceInteract), TimeSpan.FromSeconds(sinceAction), interactions);

    [Fact]
    public void TheOpenListEndsTheWait() => Assert.Equal(BellStep.Done, BellOpening.Next(State(list: true, talk: true, elapsed: 30)));

    [Fact]
    public void ATalkWindowIsAdvanced() => Assert.Equal(BellStep.AdvanceTalk, BellOpening.Next(State(talk: true, reacted: true)));

    [Fact]
    public void TalkIsAdvancedAtAPlayersPace() =>
        Assert.Equal(BellStep.Wait, BellOpening.Next(State(talk: true, reacted: true, sinceAction: 0.3)));

    [Fact]
    public void TwoTalksInARowAreBothAdvanced()
    {
        // The Mizzenmast bell: interact → Talk → Talk → retainer list.
        Assert.Equal(BellStep.AdvanceTalk, BellOpening.Next(State(talk: true, reacted: true, elapsed: 1.5)));
        Assert.Equal(BellStep.AdvanceTalk, BellOpening.Next(State(talk: true, reacted: true, elapsed: 2.5, sinceAction: 1)));
        Assert.Equal(BellStep.Wait, BellOpening.Next(State(reacted: true, elapsed: 3.5, sinceInteract: 3.5, sinceAction: 1)));
        Assert.Equal(BellStep.Done, BellOpening.Next(State(list: true, reacted: true, elapsed: 4)));
    }

    [Fact]
    public void ABellThatDidNotAnswerIsUsedAgainOnce()
    {
        Assert.Equal(BellStep.InteractAgain, BellOpening.Next(State(sinceInteract: 3.2, elapsed: 3.2)));
        Assert.Equal(BellStep.Wait, BellOpening.Next(State(sinceInteract: 3.2, elapsed: 6.5, interactions: 2)));
    }

    [Fact]
    public void ABellThatAnsweredIsNotUsedAgain() =>
        Assert.Equal(BellStep.Wait, BellOpening.Next(State(reacted: true, sinceInteract: 5, elapsed: 5)));

    [Fact]
    public void ItGivesUpAfterTheTimeout() =>
        Assert.Equal(BellStep.GiveUp, BellOpening.Next(State(reacted: true, elapsed: BellOpening.Timeout.TotalSeconds + 0.1)));

    [Fact]
    public void TheTimeoutLeavesRoomForTwoDialogues() => Assert.True(BellOpening.Timeout >= TimeSpan.FromSeconds(15));
}
