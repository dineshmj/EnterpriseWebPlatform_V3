using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;

/// <summary>
/// A verifying context rejected the application (business failure of the onboarding
/// saga). Names the evidence documents so their owner can compensate them.
/// </summary>
public sealed record OnboardingApplicationRejectedDomainEvent(
    long ApplicationId,
    Guid ApplicationRef,
    long CustomerId,
    string ApplicationNumber,
    string BranchCode,
    OnboardingRejectionStage Stage,
    OnboardingApplicationStatus PreviousStatus,
    IReadOnlyList<EvidenceDocument> EvidenceDocuments,
    DateTimeOffset OccurredAt) : IDomainEvent;