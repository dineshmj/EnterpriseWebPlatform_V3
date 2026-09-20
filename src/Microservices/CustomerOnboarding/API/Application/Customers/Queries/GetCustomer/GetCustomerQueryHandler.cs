using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;

public sealed class GetCustomerQueryHandler
{
    private readonly ICustomerReadContext _readContext;

    public GetCustomerQueryHandler(ICustomerReadContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<CustomerDetailsDto?> HandleAsync(
        GetCustomerQuery query,
        CancellationToken cancellationToken)
    {
        return await _readContext.Customers
            .AsNoTracking()
            .Where(x => x.Id == query.CustomerId)
            .Select(x => new CustomerDetailsDto(
                x.Id,
                x.CustomerNumber.Value,
                x.FirstName,
                x.LastName,
                x.Email.Value,
                x.PhoneNumber.Value,
                x.CustomerType,
                x.Status,
                x.BranchId,
                x.Version))
            .SingleOrDefaultAsync(cancellationToken);
    }
}

public sealed record CustomerDetailsDto(
    long CustomerId,
    string CustomerNumber,
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    Domain.Enums.CustomerType CustomerType,
    Domain.Enums.CustomerStatus Status,
    long? BranchId,
    long Version);