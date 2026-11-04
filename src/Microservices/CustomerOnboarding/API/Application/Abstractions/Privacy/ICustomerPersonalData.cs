using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Privacy;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The read side's way to the encrypted personal fields: the queries never see a
// │ ciphertext, a key or a column name - they ask for the details of the customers on
// │ their page, and filter by exact e-mail or surname through the blind index.
// └──────────────────────────────────────────────────────────────────────────────────────
public interface ICustomerPersonalData
{
    /// <summary>The decrypted personal details of these customers (a customer without a key is <see cref="CustomerPersonalDetails.ErasedDetails"/>).</summary>
    Task<IReadOnlyDictionary<long, CustomerPersonalDetails>> ReadAsync(IReadOnlyCollection<long> customerIds, CancellationToken cancellationToken);

    /// <summary>Customers whose e-mail address or surname is exactly <paramref name="value"/> (ignoring case and outer spaces).</summary>
    IQueryable<Customer> WhereEmailOrSurnameIs(IQueryable<Customer> customers, string value);
}

/// <summary>A customer's decrypted personal details; the residential address is the primary one, if any.</summary>
public sealed record CustomerPersonalDetails(
    bool Erased,
    string FirstName,
    string LastName,
    string Email,
    string PhoneNumber,
    ResidentialAddressDetails? ResidentialAddress)
{
    /// <summary>The customer's key was destroyed: their personal data can no longer be read.</summary>
    public static readonly CustomerPersonalDetails ErasedDetails = new(true, string.Empty, string.Empty, string.Empty, string.Empty, null);
}

public sealed record ResidentialAddressDetails(
    string AddressLine1,
    string? AddressLine2,
    string City,
    string State,
    string PostalCode,
    string CountryCode);