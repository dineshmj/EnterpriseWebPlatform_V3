namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

/// <summary>
/// Published when an onboarding application is submitted. Starts the KYC step of
/// the onboarding saga: Customer KYC opens one KYC case per application.
///
/// Other contexts must identify the application by <c>ApplicationRef</c> (a GUID
/// that never repeats); <c>ApplicationId</c> is Customer Onboarding's internal
/// database ID and restarts when its database is recreated. <c>BranchCode</c> is
/// the branch the application was opened in (KYC scopes its work queue by it).
/// CustomerNumber lets KYC locate the customer's evidence in Documents Management.
/// </summary>
public sealed record OnboardingApplicationSubmittedIntegrationEvent(
    long ApplicationId,
    Guid ApplicationRef,
    long CustomerId,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode);
