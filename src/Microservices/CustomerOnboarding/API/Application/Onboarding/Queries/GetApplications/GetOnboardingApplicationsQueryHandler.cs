using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Queries.GetApplications;

public sealed class GetOnboardingApplicationsQueryHandler
{
    private readonly IOnboardingApplicationReadContext _readContext;

    public GetOnboardingApplicationsQueryHandler(
        IOnboardingApplicationReadContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<PagedResult<OnboardingApplicationListItemDto>> HandleAsync(
        GetOnboardingApplicationsQuery query,
        CancellationToken cancellationToken)
    {
        var pageNumber = Math.Max(1, query.PageNumber);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var applications = _readContext.OnboardingApplications.AsNoTracking();

        if (query.CustomerId.HasValue)
        {
            applications = applications.Where(
                x => x.CustomerId == query.CustomerId.Value);
        }

        var totalCount = await applications.CountAsync(cancellationToken);

        var items = await applications
            .OrderByDescending(x => x.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new OnboardingApplicationListItemDto(
                x.Id,
                x.ApplicationNumber.Value,
                x.CustomerId,
                x.Status,
                x.CreatedAt,
                x.Version))
            .ToListAsync(cancellationToken);

        return new PagedResult<OnboardingApplicationListItemDto>(
            items,
            pageNumber,
            pageSize,
            totalCount);
    }
}

public sealed record OnboardingApplicationListItemDto(
    long ApplicationId,
    string ApplicationNumber,
    long CustomerId,
    Domain.Enums.OnboardingApplicationStatus Status,
    DateTimeOffset CreatedAt,
    long Version);

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);