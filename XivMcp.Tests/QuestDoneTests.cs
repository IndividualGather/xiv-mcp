using XivMcp.Quests;

namespace XivMcp.Tests;

public class QuestDoneTests
{
    [Fact]
    public void A_quest_is_done_once_completed_and_out_of_the_journal()
    {
        Assert.True(QuestDone.Finished(repeatable: false, complete: true, completedToday: false, accepted: false));
    }

    [Fact]
    public void A_quest_still_in_the_journal_is_not_done()
    {
        Assert.False(QuestDone.Finished(repeatable: false, complete: true, completedToday: false, accepted: true));
        Assert.False(QuestDone.Finished(repeatable: false, complete: false, completedToday: false, accepted: true));
    }

    [Fact]
    public void A_quest_that_is_not_complete_is_not_done_even_when_out_of_the_journal()
    {
        Assert.False(QuestDone.Finished(repeatable: false, complete: false, completedToday: true, accepted: false));
    }

    [Fact]
    public void A_daily_counts_when_completed_today_not_when_completed_some_day()
    {
        Assert.True(QuestDone.Finished(repeatable: true, complete: true, completedToday: true, accepted: false));
        Assert.False(QuestDone.Finished(repeatable: true, complete: true, completedToday: false, accepted: false));
        Assert.False(QuestDone.Finished(repeatable: true, complete: true, completedToday: true, accepted: true));
    }

    [Fact]
    public void A_reading_counts_only_after_it_was_seen_twice_in_a_row()
    {
        var steady = new SteadyReading<uint?>(2);
        Assert.Null(steady.Confirm(66959));
        Assert.Equal(66959u, steady.Confirm(66959));
    }

    [Fact]
    public void A_reading_that_flickers_starts_counting_again()
    {
        var steady = new SteadyReading<uint?>(2);
        Assert.Null(steady.Confirm(66959));
        Assert.Null(steady.Confirm(null));
        Assert.Null(steady.Confirm(66959));
        Assert.Null(steady.Confirm(66960));
        Assert.Equal(66960u, steady.Confirm(66960));
    }

    [Fact]
    public void Nothing_read_is_never_confirmed()
    {
        var steady = new SteadyReading<uint?>(2);
        Assert.Null(steady.Confirm(null));
        Assert.Null(steady.Confirm(null));
    }
}
