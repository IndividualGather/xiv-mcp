using XivMcp.Ui;

namespace XivMcp.Tests;

public class RecoveryTests
{
    private static RecoverySnapshot Stuck(params string[] windows) => new(Occupied: true, Windows: windows, Blocker: null, EscapesSent: 0, TriesOnTop: 0);

    [Fact]
    public void Nothing_to_do_when_the_character_is_free()
    {
        Assert.Equal(RecoveryStep.Done, RecoveryPlanner.Next(new RecoverySnapshot(false, [], null, 0, 0)).Step);
    }

    [Fact]
    public void Windows_are_left_alone_when_they_hold_nothing_up()
    {
        // The player's own Inventory or Character window doesn't keep them busy: not ours to close.
        Assert.Equal(RecoveryStep.Done, RecoveryPlanner.Next(new RecoverySnapshot(false, ["Inventory"], null, 0, 0)).Step);
    }

    [Fact]
    public void Combat_cutscenes_and_loading_are_never_interrupted()
    {
        var action = RecoveryPlanner.Next(new RecoverySnapshot(true, ["Talk"], "in combat", 0, 0));
        Assert.Equal(RecoveryStep.Blocked, action.Step);
        Assert.Contains("in combat", action.Reason);
    }

    [Fact]
    public void Dialogue_is_clicked_through()
    {
        Assert.Equal(new RecoveryAction(RecoveryStep.AdvanceTalk, "Talk"), RecoveryPlanner.Next(Stuck("Talk")));
    }

    [Fact]
    public void A_question_is_answered_no()
    {
        Assert.Equal(new RecoveryAction(RecoveryStep.AnswerNo, "SelectYesno"), RecoveryPlanner.Next(Stuck("SelectYesno", "SatisfactionSupply")));
    }

    [Fact]
    public void An_npc_menu_is_left()
    {
        Assert.Equal(RecoveryStep.LeaveMenu, RecoveryPlanner.Next(Stuck("SelectString")).Step);
        Assert.Equal(RecoveryStep.LeaveMenu, RecoveryPlanner.Next(Stuck("SelectIconString")).Step);
    }

    [Fact]
    public void Other_windows_are_closed_innermost_first()
    {
        Assert.Equal(new RecoveryAction(RecoveryStep.CloseWindow, "Request"), RecoveryPlanner.Next(Stuck("Request", "SatisfactionSupply", "SelectString")));
    }

    [Fact]
    public void A_window_that_does_not_close_gets_escape()
    {
        var action = RecoveryPlanner.Next(Stuck("SatisfactionSupply") with { TriesOnTop = RecoveryPlanner.TriesPerWindow });
        Assert.Equal(RecoveryStep.PressEscape, action.Step);
    }

    [Fact]
    public void Dialogue_gets_many_clicks_but_not_endless_ones()
    {
        Assert.Equal(RecoveryStep.AdvanceTalk, RecoveryPlanner.Next(Stuck("Talk") with { TriesOnTop = RecoveryPlanner.TriesPerWindow + 3 }).Step);
        Assert.Equal(RecoveryStep.PressEscape, RecoveryPlanner.Next(Stuck("Talk") with { TriesOnTop = RecoveryPlanner.MaxDialogueLines }).Step);
    }

    [Fact]
    public void Busy_with_no_window_left_gets_escape()
    {
        // The state Tiisol Ja's deliveries ended in: the event still held the player, but its window was gone.
        Assert.Equal(RecoveryStep.PressEscape, RecoveryPlanner.Next(Stuck()).Step);
    }

    [Fact]
    public void Escape_is_pressed_only_a_few_times_before_giving_up()
    {
        var action = RecoveryPlanner.Next(Stuck() with { EscapesSent = RecoveryPlanner.MaxEscapes });
        Assert.Equal(RecoveryStep.GiveUp, action.Step);
        Assert.NotNull(action.Reason);
    }

    [Fact]
    public void The_system_menu_escape_opened_is_closed_again()
    {
        // Escape with nothing left to close opens the System menu, which doesn't hold the player up.
        Assert.Equal(new RecoveryAction(RecoveryStep.CloseWindow, "SystemMenu"),
            RecoveryPlanner.Next(new RecoverySnapshot(false, ["SystemMenu"], null, 1, 0)));
    }

    [Fact]
    public void Hud_and_chat_are_not_windows_to_close()
    {
        Assert.False(RecoveryPlanner.IsClosable("_ToDoList"));
        Assert.False(RecoveryPlanner.IsClosable("ChatLogPanel_0"));
        Assert.False(RecoveryPlanner.IsClosable("NamePlate"));
        Assert.True(RecoveryPlanner.IsClosable("SatisfactionSupply"));
        Assert.Equal(RecoveryStep.PressEscape, RecoveryPlanner.Next(Stuck("ChatLog", "_DTR")).Step);
    }
}
