using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;

public sealed record OnboardingApplicationSubmittedDomainEvent(
    long ApplicationId,
    Guid ApplicationRef,
    long CustomerId,
    string ApplicationNumber,
    string BranchCode,
    DateTimeOffset OccurredAt) : IDomainEvent;