using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

public sealed record PostalAddress
{
    public string AddressLine1 { get; }
    public string? AddressLine2 { get; }
    public string City { get; }
    public string State { get; }
    public string PostalCode { get; }
    public string CountryCode { get; }

    private PostalAddress(string line1, string? line2, string city, string state,
        string postalCode, string countryCode)
    {
        AddressLine1 = line1; AddressLine2 = line2; City = city; State = state;
        PostalCode = postalCode; CountryCode = countryCode;
    }

    public static PostalAddress Create(string line1, string? line2, string city,
        string state, string postalCode, string countryCode)
    {
        if (string.IsNullOrWhiteSpace(line1)) throw new DomainRuleViolationException("Address line 1 is required.");
        if (string.IsNullOrWhiteSpace(city)) throw new DomainRuleViolationException("City is required.");
        if (string.IsNullOrWhiteSpace(state)) throw new DomainRuleViolationException("State is required.");
        if (string.IsNullOrWhiteSpace(postalCode)) throw new DomainRuleViolationException("Postal code is required.");
        if (string.IsNullOrWhiteSpace(countryCode)) throw new DomainRuleViolationException("Country code is required.");

        var cc = countryCode.Trim().ToUpperInvariant();
        if (cc.Length != 2) throw new DomainRuleViolationException("Country code must contain exactly two characters.");

        return new PostalAddress(line1.Trim(),
            string.IsNullOrWhiteSpace(line2) ? null : line2.Trim(),
            city.Trim(), state.Trim(), postalCode.Trim(), cc);
    }
}