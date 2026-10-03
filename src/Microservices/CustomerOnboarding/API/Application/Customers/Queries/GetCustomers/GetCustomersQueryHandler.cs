using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomers;

public sealed class GetCustomersQueryHandler
{
    private readonly ICustomerReadContext _readContext;

    public GetCustomersQueryHandler(ICustomerReadContext readContext)
    {
        _readContext = readContext;
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

            customers = customers.Where(x =>
                x.CustomerNumber.Value.Contains(search) ||
                x.FirstName.Contains(search) ||
                x.LastName.Contains(search) ||
                x.Email.Value.Contains(search));
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
                x.FirstName,
                x.LastName,
                Email = x.Email.Value,
                x.CustomerType,
                x.Status
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(x => new CustomerListItemDto(
                x.Id,
                x.CustomerNumber,
                x.FirstName,
                x.LastName,
                x.Email,
                x.CustomerType,
                x.Status.ToString()))
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
    string Status);

public sealed record PagedResult<T>(
    IReadOnlyCollection<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);