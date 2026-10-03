namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

public sealed record OnboardingApplicationStatusChangedIntegrationEvent(
    long ApplicationId,
    Guid ApplicationRef,
    long CustomerId,
    string PreviousStatus,
    string NewStatus);