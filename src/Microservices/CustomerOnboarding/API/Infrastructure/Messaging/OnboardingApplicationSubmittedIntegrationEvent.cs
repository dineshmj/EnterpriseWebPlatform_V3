namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

/// <summary>
/// Published when an onboarding application is submitted. Starts the KYC step of
/// the onboarding saga: Customer KYC opens one KYC case per application.
/// CustomerNumber is included (additive change) because KYC locates the
/// customer's evidence in Documents Management by customer number.
/// </summary>
public sealed record OnboardingApplicationSubmittedIntegrationEvent(
    long ApplicationId,
    long CustomerId,
    string ApplicationNumber,
    string CustomerNumber);
