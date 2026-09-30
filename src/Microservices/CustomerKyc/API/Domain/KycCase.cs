namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain;

public sealed class KycCase
{
    private KycCase() { }

    public KycCase(string customerNumber, string status, string? initiatedByUserId)
    {
        CustomerNumber = customerNumber;
        Status = status;
        InitiatedByUserId = initiatedByUserId;
        IdentityVerificationStatus = "PENDING_REVIEW";
        DocumentVerificationStatus = "PENDING_REVIEW";
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public long Id { get; private set; }

    public string CustomerNumber { get; private set; } = string.Empty;

    /// <summary>
    /// Overall KYC status. This remains PENDING_REVIEW until all mandatory
    /// verification stages are approved, or becomes REJECTED when a mandatory
    /// stage is rejected.
    /// </summary>
    public string Status { get; private set; } = string.Empty;


    public string? InitiatedByUserId { get; private set; }

    public string IdentityVerificationStatus { get; private set; } = "PENDING_REVIEW";

    public string? IdentityVerificationByUserId { get; private set; }

    public DateTimeOffset? IdentityVerificationAt { get; private set; }

    public string? IdentityVerificationRemarks { get; private set; }


    public string DocumentVerificationStatus { get; private set; } = "PENDING_REVIEW";

    public string? DocumentVerificationByUserId { get; private set; }

    public DateTimeOffset? DocumentVerificationAt { get; private set; }

    public string? DocumentVerificationRemarks { get; private set; }

    // Overall/final KYC decision metadata. Populated only when the overall
    // case becomes APPROVED or REJECTED.
    public string? DecisionByUserId { get; private set; }

    public DateTimeOffset? DecisionAt { get; private set; }

    public string? DecisionRemarks { get; private set; }


    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}