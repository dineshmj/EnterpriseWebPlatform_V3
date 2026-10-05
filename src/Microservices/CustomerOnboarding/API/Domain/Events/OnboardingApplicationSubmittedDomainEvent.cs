using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;

public sealed record OnboardingApplicationSubmittedDomainEvent(
    long ApplicationId,
    Guid ApplicationRef,
    long CustomerId,
    string ApplicationNumber,
    string BranchCode,
    IReadOnlyList<EvidenceDocument> EvidenceDocuments,
    DateTimeOffset OccurredAt) : IDomainEvent;