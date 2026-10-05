using EnterpriseWebPlatform.Accounts.Api.Domain.Common;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.Events;

/// <summary>An account application was opened for a compliance-approved onboarding application. Published.</summary>
public sealed record AccountApplicationCreatedDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>An officer took the application (claim or first action). Internal.</summary>
public sealed record AccountApplicationAssignedDomainEvent(string OfficerUserId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The assigned officer returned the application to the work queue. Internal.</summary>
public sealed record AccountApplicationReleasedDomainEvent(string OfficerUserId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The application was put on hold, or released from hold. Internal.</summary>
public sealed record AccountApplicationHoldChangedDomainEvent(bool OnHold, string OfficerUserId, string? Reason, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The officer approved the opening; the core-banking system now opens the account. Internal.</summary>
public sealed record AccountApplicationApprovedDomainEvent(string OfficerUserId, AccountProduct Product, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The officer rejected the opening (business failure of the onboarding). Published.</summary>
public sealed record AccountApplicationRejectedDomainEvent(
    AccountApplicationStatus PreviousStatus,
    string DecidedByUserId,
    string Remarks,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The core-banking system opened the account: the onboarding's last step succeeded. Published.</summary>
public sealed record AccountOpenedDomainEvent(
    string AccountNumber,
    string Bsb,
    AccountProduct Product,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The account could not be opened after approval (refused by core banking, or it kept
/// failing): work already done must be compensated. Published.
/// </summary>
public sealed record AccountOpeningFailedDomainEvent(
    string Reason,
    int Attempts,
    DateTimeOffset OccurredAt) : IDomainEvent;