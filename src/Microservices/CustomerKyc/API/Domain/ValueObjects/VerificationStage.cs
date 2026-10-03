using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

/// <summary>
/// The state of one verification stage (identity or document) and, once decided,
/// who decided it, when, and why. Immutable: a decision produces a new value.
/// Mapped by EF Core as a complex type onto the stage's columns of kyc_cases.
/// </summary>
public sealed record VerificationStage
{
    // For EF Core materialization.
    private VerificationStage()
    {
    }

    public VerificationStatus Status { get; private init; }

    public string? DecidedByUserId { get; private init; }

    public DateTimeOffset? DecidedAt { get; private init; }

    public string? Remarks { get; private init; }

    public bool IsPending => Status == VerificationStatus.PendingReview;

    public bool IsApproved => Status == VerificationStatus.Approved;

    public static VerificationStage Pending() => new() { Status = VerificationStatus.PendingReview };

    /// <summary>Records the decision. Only a pending stage can be decided.</summary>
    public VerificationStage Decide(
        VerificationStageType stage,
        StageDecision decision,
        string decidedByUserId,
        DateTimeOffset decidedAt,
        DecisionRemarks? remarks)
    {
        if (!IsPending)
        {
            throw new DomainConflictException(
                $"The {Describe(stage)} stage is no longer awaiting review.");
        }

        if (decision == StageDecision.Reject && remarks is null)
        {
            throw new DomainRuleViolationException(
                "Decision remarks are required when rejecting a verification stage.");
        }

        return new VerificationStage
        {
            Status = decision == StageDecision.Approve ? VerificationStatus.Approved : VerificationStatus.Rejected,
            DecidedByUserId = decidedByUserId,
            DecidedAt = decidedAt,
            Remarks = remarks?.Value
        };
    }

    private static string Describe(VerificationStageType stage) =>
        stage == VerificationStageType.IdentityVerification ? "identity verification" : "document verification";
}
