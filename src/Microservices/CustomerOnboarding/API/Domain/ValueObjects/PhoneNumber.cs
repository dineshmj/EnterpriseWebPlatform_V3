using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

public sealed record PhoneNumber
{
    public string Value { get; }

    private PhoneNumber(string value) => Value = value;

    public static PhoneNumber Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainRuleViolationException("Phone number is required.");

        var normalized = value.Trim();

        if (normalized.Length > 30)
            throw new DomainRuleViolationException("Phone number cannot exceed 30 characters.");

        return new PhoneNumber(normalized);
    }

    public override string ToString() => Value;
}