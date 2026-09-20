using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

public interface IOnboardingApplicationReadContext
{
    IQueryable<OnboardingApplication> OnboardingApplications { get; }
}