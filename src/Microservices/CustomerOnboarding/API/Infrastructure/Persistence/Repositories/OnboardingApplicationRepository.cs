using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Repositories;

public sealed class OnboardingApplicationRepository
    : IOnboardingApplicationRepository
{
    private readonly CustomerDbContext _dbContext;

    public OnboardingApplicationRepository(CustomerDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<OnboardingApplication?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.OnboardingApplications
            .Include(x => x.Customer)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public Task<OnboardingApplication?> GetByApplicationNumberAsync(
        string applicationNumber,
        CancellationToken cancellationToken = default)
    {
        return _dbContext.OnboardingApplications
            .SingleOrDefaultAsync(
                x => x.ApplicationNumber.Value == applicationNumber,
                cancellationToken);
    }

    public async Task<IReadOnlyList<OnboardingApplication>> GetByCustomerIdAsync(
        long customerId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.OnboardingApplications
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.CreatedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(
        OnboardingApplication application,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.OnboardingApplications
            .AddAsync(application, cancellationToken);
    }

    public void Remove(OnboardingApplication application)
    {
        _dbContext.OnboardingApplications.Remove(application);
    }
}