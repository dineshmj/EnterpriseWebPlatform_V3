using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Queries.GetApplications;

public sealed class GetOnboardingApplicationsQueryHandler
{
    private readonly IOnboardingApplicationReadContext _readContext;

    private readonly ICustomerReadContext _customerReadContext;

    public GetOnboardingApplicationsQueryHandler(
        IOnboardingApplicationReadContext readContext,
        ICustomerReadContext customerReadContext)
    {
        _readContext = readContext;
        _customerReadContext = customerReadContext;
    }

    public async Task<PagedResult<OnboardingApplicationListItemDto>> HandleAsync(
        GetOnboardingApplicationsQuery query,
        CustomerAccessScope scope,
        CancellationToken cancellationToken)
    {
        var pageNumber = Math.Max(1, query.PageNumber);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        // Only applications of customers within the caller's scope.
        var customersInScope = _customerReadContext.Customers
            .WithinScope(scope)
            .Select(customer => customer.Id);

        var applications = _readContext.OnboardingApplications
            .AsNoTracking()
            .Where(application => customersInScope.Contains(application.CustomerId));

        if (query.CustomerId.HasValue)
        {
            applications = applications.Where(
                x => x.CustomerId == query.CustomerId.Value);
        }

        var totalCount = await applications.CountAsync(cancellationToken);

        var rows = await applications
            .OrderByDescending(x => x.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                ApplicationNumber = x.ApplicationNumber.Value,
                x.CustomerId,
                x.Status,
                x.CreatedAt,
                x.Version
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(x => new OnboardingApplicationListItemDto(
                x.Id,
                x.ApplicationNumber,
                x.CustomerId,
                x.Status.ToString(),
                x.CreatedAt,
                x.Version))
            .ToList();

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
    string Status,
    DateTimeOffset CreatedAt,
    long Version);

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);