using System;
using XivMcp.Ui;

namespace XivMcp.Tests;

public class AutoRetainerStartTests
{
    private static AutoRetainerStartStep Next(bool busy = false, bool talk = false, bool list = false, double elapsed = 1, double sinceAction = 5, double sinceList = 0)
        => AutoRetainerStart.Next(new AutoRetainerStartState(busy, talk, list, TimeSpan.FromSeconds(elapsed), TimeSpan.FromSeconds(sinceAction),
            list ? TimeSpan.FromSeconds(sinceList) : null));

    [Fact]
    public void Busy_autoretainer_has_started() => Assert.Equal(AutoRetainerStartStep.Started, Next(busy: true, talk: true));

    [Fact]
    public void A_venture_report_message_is_clicked_through_not_taken_as_nothing_to_do()
    {
        // "<retainer> has completed a venture!" comes before the retainer list; AutoRetainer starts once the list is open.
        Assert.Equal(AutoRetainerStartStep.AdvanceTalk, Next(talk: true, sinceAction: 1));
    }

    [Fact]
    public void Dialogue_is_advanced_at_a_players_pace() => Assert.Equal(AutoRetainerStartStep.Wait, Next(talk: true, sinceAction: 0.2));

    [Fact]
    public void Waits_a_while_for_autoretainer_after_the_list_opened()
        => Assert.Equal(AutoRetainerStartStep.Wait, Next(list: true, sinceList: 3, elapsed: 6));

    [Fact]
    public void List_open_but_autoretainer_idle_means_nothing_to_do()
        => Assert.Equal(AutoRetainerStartStep.NothingToDo, Next(list: true, sinceList: AutoRetainerStart.ListGrace.TotalSeconds + 1, elapsed: 20));

    [Fact]
    public void No_list_and_no_start_in_time_gives_up()
        => Assert.Equal(AutoRetainerStartStep.GiveUp, Next(elapsed: AutoRetainerStart.Timeout.TotalSeconds + 1));

    [Fact]
    public void A_dialogue_that_never_leads_to_the_list_gives_up_in_time()
        => Assert.Equal(AutoRetainerStartStep.GiveUp, Next(talk: true, elapsed: AutoRetainerStart.Timeout.TotalSeconds + 1, sinceAction: 2));
}
