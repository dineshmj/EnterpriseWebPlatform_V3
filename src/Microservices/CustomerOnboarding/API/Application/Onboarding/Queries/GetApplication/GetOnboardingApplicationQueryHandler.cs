using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Queries.GetApplication;

public sealed class GetOnboardingApplicationQueryHandler
{
    private readonly IOnboardingApplicationReadContext _readContext;

    public GetOnboardingApplicationQueryHandler(
        IOnboardingApplicationReadContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<OnboardingApplicationDetailsDto?> HandleAsync(
        GetOnboardingApplicationQuery query,
        CancellationToken cancellationToken)
    {
        return await _readContext.OnboardingApplications
            .AsNoTracking()
            .Where(x => x.Id == query.ApplicationId)
            .Select(x => new OnboardingApplicationDetailsDto(
                x.Id,
                x.ApplicationNumber.Value,
                x.CustomerId,
                x.Status,
                x.SubmittedAt,
                x.CompletedAt,
                x.CreatedAt,
                x.UpdatedAt,
                x.Version))
            .SingleOrDefaultAsync(cancellationToken);
    }
}

public sealed record OnboardingApplicationDetailsDto(
    long ApplicationId,
    string ApplicationNumber,
    long CustomerId,
    Domain.Enums.OnboardingApplicationStatus Status,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);