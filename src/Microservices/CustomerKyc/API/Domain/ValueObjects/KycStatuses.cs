namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

/// <summary>Status of one verification stage.</summary>
public enum VerificationStatus
{
    PendingReview = 1,
    Approved = 2,
    Rejected = 3
}

/// <summary>Overall status of a KYC case, derived from its stages.</summary>
public enum KycCaseStatus
{
    PendingReview = 1,
    Approved = 2,
    Rejected = 3
}

/// <summary>The verification stages every KYC case must pass.</summary>
public enum VerificationStageType
{
    IdentityVerification = 1,
    DocumentVerification = 2
}

/// <summary>An officer's decision on one stage.</summary>
public enum StageDecision
{
    Approve = 1,
    Reject = 2
}

/// <summary>
/// The persisted / published codes (UPPER_SNAKE_CASE), matching the CHECK
/// constraints in EwpKycDb.sql and the Integration Event Catalogue. Codes are
/// mapped explicitly so a renamed enum member can never change a stored value.
/// </summary>
public static class KycCodes
{
    public static string ToCode(this VerificationStatus status) => status switch
    {
        VerificationStatus.PendingReview => "PENDING_REVIEW",
        VerificationStatus.Approved => "APPROVED",
        VerificationStatus.Rejected => "REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string ToCode(this KycCaseStatus status) => status switch
    {
        KycCaseStatus.PendingReview => "PENDING_REVIEW",
        KycCaseStatus.Approved => "APPROVED",
        KycCaseStatus.Rejected => "REJECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string ToCode(this VerificationStageType stage) => stage switch
    {
        VerificationStageType.IdentityVerification => "IDENTITY_VERIFICATION",
        VerificationStageType.DocumentVerification => "DOCUMENT_VERIFICATION",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };

    public static VerificationStatus ParseVerificationStatus(string code) => code switch
    {
        "PENDING_REVIEW" => VerificationStatus.PendingReview,
        "APPROVED" => VerificationStatus.Approved,
        "REJECTED" => VerificationStatus.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown verification status code.")
    };

    public static KycCaseStatus ParseCaseStatus(string code) => code switch
    {
        "PENDING_REVIEW" => KycCaseStatus.PendingReview,
        "APPROVED" => KycCaseStatus.Approved,
        "REJECTED" => KycCaseStatus.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown KYC case status code.")
    };

    public static bool TryParseCaseStatus(string? code, out KycCaseStatus status)
    {
        status = default;
        switch (code?.Trim().ToUpperInvariant())
        {
            case "PENDING_REVIEW": status = KycCaseStatus.PendingReview; return true;
            case "APPROVED": status = KycCaseStatus.Approved; return true;
            case "REJECTED": status = KycCaseStatus.Rejected; return true;
            default: return false;
        }
    }
}