using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Application.Queries;

/// <summary>
/// Read side (CQRS): projections straight from the database to DTOs, without
/// loading aggregates. Status values are returned as their stable codes.
/// </summary>
public interface IKycCaseQueries
{
    /// <summary>Cases of ONE branch only (ABAC): officers never see other branches' work.</summary>
    Task<PagedKycCasesResponse> GetCasesAsync(
        BranchCode branch,
        int pageNumber,
        int pageSize,
        KycCaseStatus? status,
        VerificationStageType? pendingStage,
        CancellationToken cancellationToken);

    /// <summary>Null when the case does not exist or belongs to another branch.</summary>
    Task<KycCaseDetail?> GetCaseAsync(long caseId, BranchCode branch, CancellationToken cancellationToken);
}

public sealed record KycCaseListItem(
    long KycCaseId,
    string CustomerNumber,
    string ApplicationNumber,
    string Status,
    string IdentityVerificationStatus,
    string? IdentityVerificationByUserId,
    DateTimeOffset? IdentityVerificationAt,
    string? IdentityVerificationRemarks,
    string DocumentVerificationStatus,
    string? DocumentVerificationByUserId,
    DateTimeOffset? DocumentVerificationAt,
    string? DocumentVerificationRemarks,
    string? InitiatedByUserId,
    string? DecisionByUserId,
    DateTimeOffset? DecisionAt,
    string? DecisionRemarks,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string BranchCode,
    string? AssignedOfficerUserId);

public sealed record PagedKycCasesResponse(
    IReadOnlyList<KycCaseListItem> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);

public sealed record KycCaseDetail(
    long KycCaseId,
    string CustomerNumber,
    string ApplicationNumber,
    string Status,
    string IdentityVerificationStatus,
    string? IdentityVerificationByUserId,
    DateTimeOffset? IdentityVerificationAt,
    string? IdentityVerificationRemarks,
    string DocumentVerificationStatus,
    string? DocumentVerificationByUserId,
    DateTimeOffset? DocumentVerificationAt,
    string? DocumentVerificationRemarks,
    string? InitiatedByUserId,
    string? DecisionByUserId,
    DateTimeOffset? DecisionAt,
    string? DecisionRemarks,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string BranchCode,
    string? AssignedOfficerUserId);
