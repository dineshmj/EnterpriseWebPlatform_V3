namespace EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

public enum ComplianceCaseStatus
{
    /// <summary>Waiting for (or retrying) the external AML / sanctions / PEP screening.</summary>
    Screening = 1,
    UnderReview = 2,
    OnHold = 3,
    Approved = 4,
    Rejected = 5
}

/// <summary>The screening provider's verdict on the customer.</summary>
public enum ScreeningOutcome
{
    Clear = 1,
    PotentialMatch = 2,
    Match = 3
}

public enum RiskRating
{
    Low = 1,
    Medium = 2,
    High = 3
}

/// <summary>
/// The risk policy: the screening verdict sets the risk, and the risk sets the
/// clearance level an officer needs to APPROVE the case (ABAC). Rejecting or
/// holding a case needs no extra clearance.
/// </summary>
public static class RiskPolicy
{
    public static RiskRating RiskFor(ScreeningOutcome outcome) => outcome switch
    {
        ScreeningOutcome.Clear => RiskRating.Low,
        ScreeningOutcome.PotentialMatch => RiskRating.Medium,
        ScreeningOutcome.Match => RiskRating.High,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    public static int RequiredClearanceToApprove(RiskRating risk) => risk switch
    {
        RiskRating.Low => 3,
        RiskRating.Medium => 4,
        RiskRating.High => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, null)
    };
}

/// <summary>
/// The persisted / published codes (UPPER_SNAKE_CASE), matching the CHECK
/// constraints in EwpComplianceDb.sql and the Integration Event Catalogue.
/// </summary>
public static class ComplianceCodes
{
    public static string ToCode(this ComplianceCaseStatus status) => status switch
    {
        ComplianceCaseStatus.Screening => "SCREENING",
        ComplianceCaseStatus.UnderReview => "UNDER_REVIEW",
        ComplianceCaseStatus.OnHold => "ON_HOLD",
        ComplianceCaseStatus.Approved => "APPROVED",
        ComplianceCaseStatus.Rejected => "REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static ComplianceCaseStatus ParseStatus(string code) => code switch
    {
        "SCREENING" => ComplianceCaseStatus.Screening,
        "UNDER_REVIEW" => ComplianceCaseStatus.UnderReview,
        "ON_HOLD" => ComplianceCaseStatus.OnHold,
        "APPROVED" => ComplianceCaseStatus.Approved,
        "REJECTED" => ComplianceCaseStatus.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown compliance case status code.")
    };

    public static bool TryParseStatus(string? code, out ComplianceCaseStatus status)
    {
        try { status = ParseStatus(code?.Trim().ToUpperInvariant() ?? string.Empty); return true; }
        catch (ArgumentOutOfRangeException) { status = default; return false; }
    }

    public static string ToCode(this ScreeningOutcome outcome) => outcome switch
    {
        ScreeningOutcome.Clear => "CLEAR",
        ScreeningOutcome.PotentialMatch => "POTENTIAL_MATCH",
        ScreeningOutcome.Match => "MATCH",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };

    public static ScreeningOutcome ParseScreeningOutcome(string code) => code switch
    {
        "CLEAR" => ScreeningOutcome.Clear,
        "POTENTIAL_MATCH" => ScreeningOutcome.PotentialMatch,
        "MATCH" => ScreeningOutcome.Match,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown screening outcome code.")
    };

    public static string ToCode(this RiskRating risk) => risk switch
    {
        RiskRating.Low => "LOW",
        RiskRating.Medium => "MEDIUM",
        RiskRating.High => "HIGH",
        _ => throw new ArgumentOutOfRangeException(nameof(risk), risk, null)
    };

    public static RiskRating ParseRiskRating(string code) => code switch
    {
        "LOW" => RiskRating.Low,
        "MEDIUM" => RiskRating.Medium,
        "HIGH" => RiskRating.High,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown risk rating code.")
    };
}