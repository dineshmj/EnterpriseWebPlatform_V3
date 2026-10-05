using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

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
        var row = await _readContext.Customers
            .AsNoTracking()
            .Where(x => x.Id == query.CustomerId)
            .Select(x => new
            {
                x.Id,
                CustomerNumber = x.CustomerNumber.Value,
                x.Name.FirstName,
                x.Name.LastName,
                Email = x.Email.Value,
                PhoneNumber = x.PhoneNumber.Value,
                x.CustomerType,
                x.Status,
                x.BranchId,
                x.Version,
                ResidentialAddress = x.Addresses
                    .Where(a => a.AddressType == AddressType.Residential)
                    .OrderByDescending(a => a.IsPrimary)
                    .Select(a => new CustomerAddressDto(
                        a.Address.AddressLine1,
                        a.Address.AddressLine2,
                        a.Address.City,
                        a.Address.State,
                        a.Address.PostalCode,
                        a.Address.CountryCode))
                    .FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new CustomerDetailsDto(
                row.Id,
                row.CustomerNumber,
                row.FirstName,
                row.LastName,
                row.Email,
                row.PhoneNumber,
                row.CustomerType,
                row.Status.ToString().ToUpperInvariant(),
                row.BranchId,
                row.Version,
                row.ResidentialAddress);
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
    // PROSPECT / ONBOARDING / ACTIVE / SUSPENDED / CLOSED (code, like the list).
    string Status,
    long? BranchId,
    long Version,
    // The primary residential address: shown read-only when onboarding an existing customer.
    CustomerAddressDto? ResidentialAddress);

public sealed record CustomerAddressDto(
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string CountryCode);