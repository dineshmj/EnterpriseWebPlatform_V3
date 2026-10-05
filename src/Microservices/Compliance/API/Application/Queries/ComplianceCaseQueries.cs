using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Application.Queries;

/// <summary>Read side (CQRS): projections for the work queue and the case view, one branch only (ABAC).</summary>
public interface IComplianceCaseQueries
{
    Task<PagedComplianceCasesResponse> GetCasesAsync(
        BranchCode branch, int pageNumber, int pageSize, ComplianceCaseStatus? status, CancellationToken cancellationToken);

    /// <summary>Null when the case does not exist or belongs to another branch.</summary>
    Task<ComplianceCaseDetail?> GetCaseAsync(long caseId, BranchCode branch, CancellationToken cancellationToken);
}

public sealed record ComplianceCaseDetail(
    long ComplianceCaseId,
    string ApplicationNumber,
    string CustomerNumber,
    long KycCaseId,
    string BranchCode,
    string Status,
    string? ScreeningOutcome,
    string? ScreeningProvider,
    string? ScreeningReference,
    DateTimeOffset? ScreenedAt,
    int ScreeningAttempts,
    DateTimeOffset? NextScreeningAt,
    string? LastScreeningError,
    string? RiskRating,
    int? RequiredClearance,
    string? InitiatedByUserId,
    string? KycIdentityDecidedByUserId,
    string? KycDocumentDecidedByUserId,
    string? AssignedOfficerUserId,
    string? HoldReason,
    string? DecisionByUserId,
    DateTimeOffset? DecisionAt,
    string? DecisionRemarks,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CustomerName,
    ApplicantAddress ResidentialAddress,
    StaffLanIds Staff);

/// <summary>The LAN IDs of the people on the case (null when not known to this context).</summary>
public sealed record StaffLanIds(
    string? InitiatedBy,
    string? KycIdentityDecidedBy,
    string? KycDocumentDecidedBy,
    string? AssignedOfficer,
    string? DecisionBy);

/// <summary>The applicant's residential address as KYC verified it (snapshot).</summary>
public sealed record ApplicantAddress(
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? State,
    string? PostalCode,
    string? CountryCode);

public sealed record PagedComplianceCasesResponse(
    IReadOnlyList<ComplianceCaseDetail> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);