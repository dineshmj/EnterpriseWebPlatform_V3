using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;

public sealed record OnboardingApplicationSubmittedDomainEvent(
    long ApplicationId,
    long CustomerId,
    string ApplicationNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;