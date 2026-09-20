using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

public interface IOnboardingApplicationRepository
{
    Task<OnboardingApplication?> GetByIdAsync(long id, CancellationToken cancellationToken);

    Task AddAsync(OnboardingApplication application, CancellationToken cancellationToken);
}