namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

public sealed record OnboardingApplicationStatusChangedIntegrationEvent(
    long ApplicationId,
    long CustomerId,
    string PreviousStatus,
    string NewStatus);