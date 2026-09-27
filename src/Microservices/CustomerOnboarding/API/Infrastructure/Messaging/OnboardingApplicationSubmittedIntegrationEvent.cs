namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

public sealed record OnboardingApplicationSubmittedIntegrationEvent(
    long ApplicationId,
    long CustomerId,
    string ApplicationNumber);