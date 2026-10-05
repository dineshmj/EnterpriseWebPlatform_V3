using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

// HTTP contract of the KYC API (consumed by the KYC BFF). Kept separate from the
// domain types so the domain can evolve without changing the wire format.

/// <summary>Stage names as the BFF sends them in the "stage" query parameter.</summary>
public enum KycVerificationStage
{
    IdentityVerification,
    DocumentVerification
}

public sealed record KycCaseDecisionRequest(string? DecisionRemarks);

public sealed record KycCaseDecisionResponse(
    long KycCaseId,
    string CustomerNumber,
    KycVerificationStage Stage,
    string StageStatus,
    string OverallStatus,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks);

public sealed record KycCaseAssignmentResponse(long KycCaseId, string? AssignedOfficerUserId);

public sealed record OpenKycCaseRequest(
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string? InitiatedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    ApplicantContract? Applicant,
    IReadOnlyList<EvidenceDocumentContract>? EvidenceDocuments);

/// <summary>The applicant as submitted to Customer Onboarding (name and residential address).</summary>
public sealed record ApplicantContract(string? FirstName, string? LastName, AddressContract? ResidentialAddress);

public sealed record AddressContract(
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryCode);

/// <summary>A Documents Management document submitted as evidence (DocumentType KYCProof / TaxProof).</summary>
public sealed record EvidenceDocumentContract(Guid DocumentId, string? DocumentType);

public static class KycVerificationStageMapping
{
    public static VerificationStageType ToDomain(this KycVerificationStage stage) => stage switch
    {
        KycVerificationStage.IdentityVerification => VerificationStageType.IdentityVerification,
        KycVerificationStage.DocumentVerification => VerificationStageType.DocumentVerification,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };

    public static KycVerificationStage FromDomain(VerificationStageType stage) => stage switch
    {
        VerificationStageType.IdentityVerification => KycVerificationStage.IdentityVerification,
        VerificationStageType.DocumentVerification => KycVerificationStage.DocumentVerification,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };
}