using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Authorization;

// ┌─ :: SECURITY :: Scope filtering in the database ───────────────────────────────────────────────
// │ Why : Loading everything and hiding rows in the UI leaks data and does not scale.
// │ How : WithinScope adds the branch-city filter to the query itself, so out-of-scope rows are
// │       never even loaded; paging happens after the filter.
// │ Ref : Features 2.2.3 | OWASP API1 BOLA, API3 (excessive data exposure)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// The set of customers a caller may see (ABAC, branch scope).
///
/// A global scope sees every customer. A branch scope sees only customers whose
/// primary residential address is in the branch's city and country. A scope
/// without a city or country sees nothing (fail closed).
/// </summary>
public sealed record CustomerAccessScope(
    bool IsGlobal,
    string? BranchCode,
    string? BranchCity,
    string? BranchCountryCode)
{
    public static CustomerAccessScope None { get; } = new(false, null, null, null);

    public bool AllowsLocation(string city, string countryCode) =>
        IsGlobal ||
        (!string.IsNullOrWhiteSpace(BranchCity) &&
         !string.IsNullOrWhiteSpace(BranchCountryCode) &&
         string.Equals(city.Trim(), BranchCity, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(countryCode.Trim(), BranchCountryCode, StringComparison.OrdinalIgnoreCase));
}

public static class CustomerAccessScopeQueryExtensions
{
    /// <summary>Restricts a customer query to the customers within the scope.</summary>
    public static IQueryable<Customer> WithinScope(
        this IQueryable<Customer> customers,
        CustomerAccessScope scope)
    {
        if (scope.IsGlobal)
            return customers;

        if (string.IsNullOrWhiteSpace(scope.BranchCity) ||
            string.IsNullOrWhiteSpace(scope.BranchCountryCode))
            return customers.Where(_ => false);

        var city = scope.BranchCity.Trim().ToUpper();
        var countryCode = scope.BranchCountryCode.Trim().ToUpper();

        return customers.Where(customer => customer.Addresses.Any(address =>
            address.AddressType == AddressType.Residential &&
            address.IsPrimary &&
            address.Address.City.ToUpper() == city &&
            address.Address.CountryCode == countryCode));
    }
}