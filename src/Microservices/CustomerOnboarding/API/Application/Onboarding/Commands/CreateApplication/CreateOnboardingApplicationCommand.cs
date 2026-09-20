namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.CreateApplication;

public sealed record CreateOnboardingApplicationCommand(
    long CustomerId,
    string ApplicationNumber);