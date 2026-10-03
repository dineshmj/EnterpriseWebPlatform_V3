using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;

/// <summary>
/// The customer's lifecycle status changed (PROSPECT → ONBOARDING → ACTIVE, SUSPENDED…).
/// Internal to Customer Onboarding for now: no published contract exists yet, so the
/// integration-event mapper deliberately does not publish it.
/// </summary>
public sealed record CustomerStatusChangedDomainEvent(
    long CustomerId,
    CustomerStatus PreviousStatus,
    CustomerStatus NewStatus,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The customer's e-mail address and/or phone number changed. Internal for now
/// (see <see cref="CustomerStatusChangedDomainEvent"/>).
/// </summary>
public sealed record CustomerContactDetailsChangedDomainEvent(
    long CustomerId,
    DateTimeOffset OccurredAt) : IDomainEvent;