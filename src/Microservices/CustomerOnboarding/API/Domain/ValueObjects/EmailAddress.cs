using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

public sealed record EmailAddress
{
    public string Value { get; }

    private EmailAddress(string value) => Value = value;

    public static EmailAddress Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainRuleViolationException("Email address is required.");

        var normalized = value.Trim();

        if (normalized.Length > 254 || !normalized.Contains('@', StringComparison.Ordinal))
            throw new DomainRuleViolationException("Invalid email address.");

        return new EmailAddress(normalized.ToLowerInvariant());
    }

    public override string ToString() => Value;
}