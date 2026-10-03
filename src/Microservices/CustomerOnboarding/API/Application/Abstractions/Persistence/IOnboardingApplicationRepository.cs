using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

public interface IOnboardingApplicationRepository
{
    Task<OnboardingApplication?> GetByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>Looks an application up by the reference other bounded contexts use.</summary>
    Task<OnboardingApplication?> GetByRefAsync(Guid applicationRef, CancellationToken cancellationToken);

    Task AddAsync(OnboardingApplication application, CancellationToken cancellationToken);
}

/// <summary>Issues the next value of the application-number sequence.</summary>
public interface IApplicationNumberGenerator
{
    Task<long> GetNextAsync(CancellationToken cancellationToken);
}