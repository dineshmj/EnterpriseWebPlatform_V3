using EnterpriseWebPlatform.Payments.Api.Domain.Common;

namespace EnterpriseWebPlatform.Payments.Api.Domain.Events;

// ------------------------------------------------------------------ Payment facts (published on payments.payment.events)

/// <summary>The funds are reserved and the payment waits for a payments officer (new work). Published.</summary>
public sealed record PaymentApprovalRequiredDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The payment was sent and the funds debited. Published.</summary>
public sealed record PaymentCompletedDomainEvent(string NetworkReference, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Not paid, nothing left held (no funds, or the approver said no). Published.</summary>
public sealed record PaymentRejectedDomainEvent(string ReasonCode, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Not paid after the funds were reserved; the reservation was released. Published.</summary>
public sealed record PaymentFailedDomainEvent(string ReasonCode, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The release of the reserved funds kept failing: operations must act. Published.</summary>
public sealed record PaymentCompensationFailedDomainEvent(string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

// ------------------------------------------------------------------ Saga commands (sent on accounts.commands)

/// <summary>
/// The orchestrator asks Accounts to reserve, settle or release the payment's funds. The
/// command's MessageId is chosen here, so the saga knows which command it waits for.
/// </summary>
public sealed record FundsCommandIssuedDomainEvent(
    string CommandType,
    Guid MessageId,
    Guid? CausationId,
    Guid PaymentRef,
    string PaymentNumber,
    string CustomerNumber,
    string Bsb,
    string AccountNumber,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;