using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Application.Queries;

public sealed record PaymentSummary(
    long PaymentId,
    string PaymentNumber,
    string CustomerNumber,
    decimal Amount,
    string Currency,
    string PayeeName,
    string ToBsb,
    string ToAccountNumber,
    string Status,
    bool ApprovalRequired,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string InitiatedByUserId,
    string? InitiatedByLanId);

/// <summary>One line of the payment's timeline: what the saga did or received, and when.</summary>
public sealed record SagaTimelineEntry(DateTimeOffset At, string Step, string Kind, string Detail, Guid? MessageId);

/// <summary>The saga behind the payment: where it is, and everything it did.</summary>
public sealed record SagaView(
    Guid SagaId,
    string Step,
    string Status,
    int Attempts,
    DateTimeOffset? NextCheckAt,
    string? LastError,
    Guid WorkflowId,
    Guid CorrelationId,
    IReadOnlyList<SagaTimelineEntry> Timeline);

public sealed record PaymentDetail(
    long PaymentId,
    Guid PaymentRef,
    string PaymentNumber,
    string CustomerNumber,
    string FromBsb,
    string FromAccountNumber,
    string PayeeName,
    string ToBsb,
    string ToAccountNumber,
    decimal Amount,
    string Currency,
    string? Reference,
    string BranchCode,
    string Status,
    bool ApprovalRequired,
    string? OutcomeCode,
    string? OutcomeReason,
    string? NetworkReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? EndedAt,
    string InitiatedByUserId,
    string? InitiatedByLanId,
    SagaView? Saga,
    string? DecisionByUserId,
    string? DecisionByLanId,
    DateTimeOffset? DecisionAt,
    string? DecisionRemarks);

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);

/// <summary>A saga that operations should look at, with its payment.</summary>
public sealed record ProcessingItem(
    long PaymentId,
    string PaymentNumber,
    string BranchCode,
    decimal Amount,
    string Currency,
    string PayeeName,
    string PaymentStatus,
    string SagaStep,
    string SagaStatus,
    int Attempts,
    DateTimeOffset? NextCheckAt,
    string? LastError,
    DateTimeOffset UpdatedAt,
    bool Overdue);

/// <summary>The Payment Processing Monitor: counts, and the sagas that are not finished.</summary>
public sealed record ProcessingOverview(
    int CompensationFailed,
    int Overdue,
    int Retrying,
    int WaitingForApproval,
    int Running,
    IReadOnlyList<ProcessingItem> Items);

/// <summary>
/// Read side. ABAC: staff see only their own branch's payments (branch given); operations and
/// auditors, whose work is not branch-bound, see all branches (branch null).
/// </summary>
public interface IPaymentsQueries
{
    Task<PagedResponse<PaymentSummary>> GetPaymentsAsync(
        BranchCode? branch, int pageNumber, int pageSize, PaymentStatus? status, CancellationToken cancellationToken);

    Task<PaymentDetail?> GetPaymentAsync(long paymentId, BranchCode? branch, CancellationToken cancellationToken);

    /// <summary>The payment's ID for its number (e.g. PAY-261008-8KQNFR), within the caller's scope; null when none.</summary>
    Task<long?> FindPaymentIdAsync(string paymentNumber, BranchCode? branch, CancellationToken cancellationToken);

    /// <summary>Every saga that is not finished, most urgent first (stuck, overdue, retrying, waiting, running).</summary>
    Task<ProcessingOverview> GetProcessingAsync(BranchCode? branch, DateTimeOffset now, CancellationToken cancellationToken);
}