using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

public sealed record CustomerNumber
{
    public string Value { get; }

    private CustomerNumber(string value) => Value = value;

    public static CustomerNumber Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainRuleViolationException("Customer number is required.");

        var normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length > 30)
            throw new DomainRuleViolationException("Customer number cannot exceed 30 characters.");

        return new CustomerNumber(normalized);
    }

    public override string ToString() => Value;
}