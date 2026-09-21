using EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;

public sealed record CustomerCreatedDomainEvent(
    string CustomerNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;
