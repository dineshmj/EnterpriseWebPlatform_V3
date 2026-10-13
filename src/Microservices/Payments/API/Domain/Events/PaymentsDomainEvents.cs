using EnterpriseWebPlatform.Payments.Api.Domain.Common;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

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

/// <summary>
/// The payment network has not confirmed the payment for longer than the alert time: the funds
/// stay reserved and operations must reconcile it (new work). Raised once per wait. Published.
/// </summary>
public sealed record PaymentUnconfirmedByNetworkDomainEvent(DateTimeOffset UnconfirmedSince, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Manual reconciliation of a payment the network never confirmed: a resolution PROPOSED by one
/// person, then APPROVED or REJECTED by a second (four eyes). Published, so the audit trail
/// records each person and the evidence reference.
/// </summary>
public sealed record NetworkResolutionDomainEvent(
    string Stage,
    NetworkOutcome Outcome,
    string? NetworkReference,
    string Evidence,
    string ProposedByUserId,
    string ActorUserId,
    string? Remarks,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public const string Proposed = "PROPOSED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

// ------------------------------------------------------------------ Saga commands (sent on accounts.commands)

/// <summary>
/// The orchestrator asks Accounts to reserve, settle or release the payment's funds. The
/// command's MessageId is chosen here, so the saga knows which command it waits for.
/// BranchCode is the initiating staff member's branch: Accounts refuses to reserve funds on
/// an account held at another branch.
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
    string BranchCode,
    DateTimeOffset OccurredAt) : IDomainEvent;