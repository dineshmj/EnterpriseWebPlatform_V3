using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomers;

public sealed class GetCustomersQueryHandler
{
    private readonly ICustomerReadContext _readContext;

    private readonly IOnboardingApplicationReadContext _applications;

    private readonly ICustomerPersonalData _personalData;

    public GetCustomersQueryHandler(
        ICustomerReadContext readContext,
        IOnboardingApplicationReadContext applications,
        ICustomerPersonalData personalData)
    {
        _readContext = readContext;
        _applications = applications;
        _personalData = personalData;
    }

    public async Task<PagedResult<CustomerListItemDto>> HandleAsync(
        GetCustomersQuery query,
        CustomerAccessScope scope,
        CancellationToken cancellationToken)
    {
        var pageNumber = Math.Max(1, query.PageNumber);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var customers = _readContext.Customers
            .AsNoTracking()
            .WithinScope(scope);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Names and e-mail addresses are encrypted, so SQL cannot search INSIDE them: the
            // │ customer number matches in part, an e-mail address or a surname only exactly
            // │ (ignoring case), through the blind index. "(string)(object)x.CustomerNumber" is
            // │ how EF Core addresses the value object's stored column.
            // └──────────────────────────────────────────────────────────────────────────────────────
            var byNumber = customers.Where(x => ((string)(object)x.CustomerNumber).Contains(search));
            customers = byNumber.Union(_personalData.WhereEmailOrSurnameIs(customers, search));
        }

        var totalCount = await customers.CountAsync(cancellationToken);

        var rows = await customers
            .OrderByDescending(x => x.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                CustomerNumber = x.CustomerNumber.Value,
                x.CustomerType,
                x.Status
            })
            .ToListAsync(cancellationToken);

        // ┌─ PERF ───────────────────────────────────────────────────────────────────────────────
        // │ The latest application of each customer on this page (one query, this page only),
        // │ so a PROSPECT whose application was rejected says so instead of looking brand new.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var customerIds = rows.Select(x => x.Id).ToList();
        var latestApplications = (await _applications.OnboardingApplications
                .AsNoTracking()
                .Where(a => customerIds.Contains(a.CustomerId))
                .Select(a => new { a.CustomerId, ApplicationNumber = a.ApplicationNumber.Value, a.Status, a.UpdatedAt, a.CreatedAt })
                .ToListAsync(cancellationToken))
            .GroupBy(a => a.CustomerId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.CreatedAt).First());

        // SEC: only the customers on this page have their name and e-mail decrypted.
        var people = await _personalData.ReadAsync(customerIds, cancellationToken);

        var items = rows
            .Select(x => new CustomerListItemDto(
                x.Id,
                x.CustomerNumber,
                people[x.Id].FirstName,
                people[x.Id].LastName,
                people[x.Id].Email,
                x.CustomerType,
                x.Status.ToString().ToUpperInvariant(),
                latestApplications.TryGetValue(x.Id, out var latest)
                    ? new LatestApplicationDto(latest.ApplicationNumber, latest.Status.ToString(), latest.UpdatedAt)
                    : null))
            .ToList();

        return new PagedResult<CustomerListItemDto>(
            items,
            pageNumber,
            pageSize,
            totalCount);
    }
}

public sealed record CustomerListItemDto(
    long CustomerId,
    string CustomerNumber,
    string FirstName,
    string LastName,
    string Email,
    Domain.Enums.CustomerType CustomerType,
    // PROSPECT / ONBOARDING / ACTIVE / SUSPENDED / CLOSED: tells the UI whether onboarding can start.
    string Status,
    // The customer's most recent onboarding application (null: none yet).
    LatestApplicationDto? LatestApplication);

/// <summary>The outcome of a customer's most recent onboarding application, for the customer list.</summary>
public sealed record LatestApplicationDto(string ApplicationNumber, string Status, DateTimeOffset UpdatedAt);

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);