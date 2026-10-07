using System;

namespace XivMcp.Shops;

/// <summary>The player's answer to a standing spending approval request. Approvals saved before requests could wait count as approved.</summary>
public enum ApprovalAnswer { Approved, Waiting, Declined, Unanswered }

public enum ApprovalStatus { Waiting, Declined, Unanswered, Revoked, Expired, UsedUp, Active }

/// <summary>
/// A standing approval's state. A request is asked in game and answered later (the call that asked doesn't wait for the player), so
/// it starts out waiting; only an approved one that is neither revoked, expired nor used up can be spent from.
/// </summary>
public static class ApprovalState
{
    public static ApprovalStatus Of(ApprovalAnswer answer, bool revoked, DateTime expiresUtc, long remaining, DateTime nowUtc) =>
        revoked ? ApprovalStatus.Revoked
        : answer switch
        {
            ApprovalAnswer.Waiting => ApprovalStatus.Waiting,
            ApprovalAnswer.Declined => ApprovalStatus.Declined,
            ApprovalAnswer.Unanswered => ApprovalStatus.Unanswered,
            _ => nowUtc >= expiresUtc ? ApprovalStatus.Expired : remaining <= 0 ? ApprovalStatus.UsedUp : ApprovalStatus.Active,
        };

    public static string Name(ApprovalStatus status) => status switch
    {
        ApprovalStatus.Waiting => "waiting for the player",
        ApprovalStatus.Declined => "declined",
        ApprovalStatus.Unanswered => "not answered in time",
        ApprovalStatus.Revoked => "revoked",
        ApprovalStatus.Expired => "expired",
        ApprovalStatus.UsedUp => "used up",
        _ => "active",
    };
}
