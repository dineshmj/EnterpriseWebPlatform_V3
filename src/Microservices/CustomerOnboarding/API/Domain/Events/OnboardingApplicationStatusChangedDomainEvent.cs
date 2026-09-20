using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;

public sealed record OnboardingApplicationStatusChangedDomainEvent(
    long ApplicationId,
    long CustomerId,
    OnboardingApplicationStatus PreviousStatus,
    OnboardingApplicationStatus NewStatus,
    DateTimeOffset OccurredAt) : IDomainEvent;