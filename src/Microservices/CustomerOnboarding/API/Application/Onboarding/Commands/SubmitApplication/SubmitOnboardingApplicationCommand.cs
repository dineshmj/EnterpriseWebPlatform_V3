namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.SubmitApplication;

public sealed record SubmitOnboardingApplicationCommand(
    long ApplicationId,
    long ExpectedVersion);