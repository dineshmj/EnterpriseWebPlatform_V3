namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Common;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}