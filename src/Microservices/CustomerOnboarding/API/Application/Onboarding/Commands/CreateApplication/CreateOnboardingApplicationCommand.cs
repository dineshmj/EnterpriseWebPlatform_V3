namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.CreateApplication;

/// <summary>
/// Opens an onboarding application for a customer. The application number is
/// issued by Customer Onboarding; <paramref name="BranchCode"/> is the acting
/// agent's branch, taken from their token by the API layer.
/// </summary>
public sealed record CreateOnboardingApplicationCommand(
    long CustomerId,
    string BranchCode);
