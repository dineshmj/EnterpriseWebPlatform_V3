using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

public sealed record ApplicationNumber
{
    public string Value { get; }

    private ApplicationNumber(string value) => Value = value;

    public static ApplicationNumber Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainRuleViolationException("Application number is required.");

        var normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length > 30)
            throw new DomainRuleViolationException("Application number cannot exceed 30 characters.");

        return new ApplicationNumber(normalized);
    }

    public override string ToString() => Value;
}