using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

/// <summary>
/// The applicant as Customer Onboarding submitted them: the identity the KYC officer
/// verifies the evidence against. A snapshot, never refreshed - the case must keep
/// showing what was verified even if the customer's record changes later. Holds only
/// what KYC needs (name and residential address; no contact details).
/// </summary>
public sealed class Applicant
{
    public const int MaxNameLength = 100;

    // For EF Core materialization.
    private Applicant()
    {
        FirstName = null!;
        LastName = null!;
    }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public string? AddressLine1 { get; private set; }

    public string? AddressLine2 { get; private set; }

    public string? City { get; private set; }

    public string? State { get; private set; }

    public string? PostalCode { get; private set; }

    public string? CountryCode { get; private set; }

    public string FullName => $"{FirstName} {LastName}";

    public static Applicant Create(
        string? firstName,
        string? lastName,
        string? addressLine1,
        string? addressLine2,
        string? city,
        string? state,
        string? postalCode,
        string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            throw new DomainRuleViolationException("The applicant's first and last name are required.");

        if (firstName.Trim().Length > MaxNameLength || lastName.Trim().Length > MaxNameLength)
            throw new DomainRuleViolationException($"The applicant's names are limited to {MaxNameLength} characters.");

        return new Applicant
        {
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            AddressLine1 = Clean(addressLine1, 200),
            AddressLine2 = Clean(addressLine2, 200),
            City = Clean(city, 100),
            State = Clean(state, 100),
            PostalCode = Clean(postalCode, 20),
            CountryCode = Clean(countryCode, 2)?.ToUpperInvariant()
        };
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new DomainRuleViolationException($"An applicant address field exceeds {maxLength} characters.");
    }
}