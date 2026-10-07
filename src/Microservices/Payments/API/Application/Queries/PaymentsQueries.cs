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

/// <summary>Read side: branch-scoped (ABAC) - a staff member sees only their own branch's payments.</summary>
public interface IPaymentsQueries
{
    Task<PagedResponse<PaymentSummary>> GetPaymentsAsync(
        BranchCode branch, int pageNumber, int pageSize, PaymentStatus? status, CancellationToken cancellationToken);

    Task<PaymentDetail?> GetPaymentAsync(long paymentId, BranchCode branch, CancellationToken cancellationToken);
}