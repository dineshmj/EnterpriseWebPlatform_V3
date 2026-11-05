using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The erasure's facts carry identifiers, people and a reason CODE only - never a name,
// │ an address or the decider's free-text remarks - so the events and the audit trail
// │ that records them hold nothing that would itself need erasing.
// └──────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// An operator asked for a customer's personal data to be erased; a second person must decide.
/// (The request's ID is assigned on save: the Outbox mapper takes it from the saved request.)
/// </summary>
public sealed record PersonalDataErasureRequestedDomainEvent(
    long CustomerId,
    string CustomerNumber,
    PersonalDataErasureReason Reason,
    string RequestedByUserId,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A second person refused the erasure: nothing was erased.</summary>
public sealed record PersonalDataErasureRejectedDomainEvent(
    long ErasureRequestId,
    long CustomerId,
    string CustomerNumber,
    PersonalDataErasureReason Reason,
    string RequestedByUserId,
    string DecidedByUserId,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A second person approved the erasure and the customer's key was destroyed in the same
/// transaction: their personal data can no longer be read anywhere it is stored encrypted.
/// </summary>
public sealed record CustomerPersonalDataErasedDomainEvent(
    long ErasureRequestId,
    long CustomerId,
    string CustomerNumber,
    PersonalDataErasureReason Reason,
    string RequestedByUserId,
    string DecidedByUserId,
    DateTimeOffset OccurredAt) : IDomainEvent;