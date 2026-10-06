using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Application.Queries;

/// <summary>Read side (CQRS): projections for the work queue, the application view and the accounts, one branch only (ABAC).</summary>
public interface IAccountsQueries
{
    Task<PagedResponse<AccountApplicationDetail>> GetApplicationsAsync(
        BranchCode branch, int pageNumber, int pageSize, AccountApplicationStatus? status, CancellationToken cancellationToken);

    /// <summary>Null when the application does not exist or belongs to another branch.</summary>
    Task<AccountApplicationDetail?> GetApplicationAsync(long applicationId, BranchCode branch, CancellationToken cancellationToken);

    Task<PagedResponse<AccountDetail>> GetAccountsAsync(BranchCode branch, int pageNumber, int pageSize, CancellationToken cancellationToken);

    /// <summary>Null when the account does not exist or belongs to another branch.</summary>
    Task<AccountDetail?> GetAccountAsync(long accountId, BranchCode branch, CancellationToken cancellationToken);
}

public sealed record AccountApplicationDetail(
    long AccountApplicationId,
    string ApplicationNumber,
    string CustomerNumber,
    long ComplianceCaseId,
    string BranchCode,
    string Status,
    string? Product,
    string? InitiatedByUserId,
    string? ComplianceApprovedByUserId,
    string? AssignedOfficerUserId,
    string? HoldReason,
    string? DecisionByUserId,
    DateTimeOffset? DecisionAt,
    string? DecisionRemarks,
    int OpeningAttempts,
    DateTimeOffset? NextOpeningAt,
    string? LastOpeningError,
    string? AccountNumber,
    DateTimeOffset? OpenedAt,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CustomerName,
    StaffLanIds Staff);

/// <summary>The LAN IDs of the people on the application (null when not known to this context).</summary>
public sealed record StaffLanIds(
    string? InitiatedBy,
    string? ComplianceApprovedBy,
    string? AssignedOfficer,
    string? DecisionBy);

public sealed record AccountDetail(
    long AccountId,
    string AccountNumber,
    string Bsb,
    string CustomerNumber,
    string BranchCode,
    string Product,
    string Status,
    string CoreBankingReference,
    DateTimeOffset OpenedAt,
    string HolderName,
    string Currency,
    decimal Balance,
    decimal HeldAmount,
    decimal Available);

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);