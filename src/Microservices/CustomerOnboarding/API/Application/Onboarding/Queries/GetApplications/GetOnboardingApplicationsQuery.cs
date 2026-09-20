namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Queries.GetApplications;

public sealed record GetOnboardingApplicationsQuery(
    int PageNumber = 1,
    int PageSize = 25,
    long? CustomerId = null);