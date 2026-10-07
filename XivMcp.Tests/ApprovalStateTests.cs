using XivMcp.Shops;

namespace XivMcp.Tests;

public class ApprovalStateTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = Now.AddHours(2);

    [Fact]
    public void An_approval_the_player_has_not_answered_yet_is_waiting()
    {
        Assert.Equal(ApprovalStatus.Waiting, ApprovalState.Of(ApprovalAnswer.Waiting, revoked: false, Later, remaining: 3500, Now));
    }

    [Fact]
    public void An_approved_one_is_active_until_it_expires_or_is_used_up()
    {
        Assert.Equal(ApprovalStatus.Active, ApprovalState.Of(ApprovalAnswer.Approved, false, Later, 3500, Now));
        Assert.Equal(ApprovalStatus.UsedUp, ApprovalState.Of(ApprovalAnswer.Approved, false, Later, 0, Now));
        Assert.Equal(ApprovalStatus.Expired, ApprovalState.Of(ApprovalAnswer.Approved, false, Now.AddMinutes(-1), 3500, Now));
    }

    [Fact]
    public void A_declined_or_unanswered_request_never_becomes_active()
    {
        Assert.Equal(ApprovalStatus.Declined, ApprovalState.Of(ApprovalAnswer.Declined, false, Later, 3500, Now));
        Assert.Equal(ApprovalStatus.Unanswered, ApprovalState.Of(ApprovalAnswer.Unanswered, false, Later, 3500, Now));
    }

    [Fact]
    public void Revoking_wins_over_everything()
    {
        Assert.Equal(ApprovalStatus.Revoked, ApprovalState.Of(ApprovalAnswer.Waiting, true, Later, 3500, Now));
        Assert.Equal(ApprovalStatus.Revoked, ApprovalState.Of(ApprovalAnswer.Approved, true, Later, 3500, Now));
    }

    [Fact]
    public void States_have_the_names_list_approvals_shows()
    {
        Assert.Equal("waiting for the player", ApprovalState.Name(ApprovalStatus.Waiting));
        Assert.Equal("used up", ApprovalState.Name(ApprovalStatus.UsedUp));
        Assert.Equal("active", ApprovalState.Name(ApprovalStatus.Active));
    }
}
