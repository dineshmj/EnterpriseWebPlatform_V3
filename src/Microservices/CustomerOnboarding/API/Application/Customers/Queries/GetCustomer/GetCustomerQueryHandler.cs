using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;

public sealed class GetCustomerQueryHandler
{
    private readonly ICustomerReadContext _readContext;
    private readonly ICustomerPersonalData _personalData;

    public GetCustomerQueryHandler(ICustomerReadContext readContext, ICustomerPersonalData personalData)
    {
        _readContext = readContext;
        _personalData = personalData;
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
                x.CustomerType,
                x.Status,
                x.BranchId,
                x.Version,
                x.CreatedAt
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
            return null;

        // SEC: the name, contact details and address lines are encrypted; only this customer's are opened.
        var person = (await _personalData.ReadAsync([row.Id], cancellationToken))[row.Id];
        var address = person.ResidentialAddress;

        return new CustomerDetailsDto(
            row.Id,
            row.CustomerNumber,
            person.FirstName,
            person.LastName,
            person.Email,
            person.PhoneNumber,
            row.CustomerType,
            row.Status.ToString().ToUpperInvariant(),
            row.BranchId,
            row.Version,
            address is null
                ? null
                : new CustomerAddressDto(
                    address.AddressLine1,
                    address.AddressLine2,
                    address.City,
                    address.State,
                    address.PostalCode,
                    address.CountryCode),
            row.CreatedAt,
            person.Erased);
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
    CustomerAddressDto? ResidentialAddress,
    // When the customer was registered (BIAN /v2: the directory entry's OpenDate).
    DateTimeOffset CreatedAt,
    // True once the customer's key was destroyed: the personal fields above are then empty.
    bool PersonalDataErased);

public sealed record CustomerAddressDto(
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string CountryCode);