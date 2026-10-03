using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

/// <summary>
/// Human-readable business number of an onboarding application, issued by Customer
/// Onboarding itself: APP-yyyyMMdd-nnnnnn (date of issue + database sequence).
/// </summary>
public sealed record ApplicationNumber
{
    public const int MaxLength = 30;

    public string Value { get; }

    private ApplicationNumber(string value) => Value = value;

    /// <summary>Issues a new number from the next value of the application-number sequence.</summary>
    public static ApplicationNumber Issue(long sequenceValue, DateTimeOffset now)
    {
        if (sequenceValue <= 0)
            throw new DomainRuleViolationException("The application number sequence must be positive.");

        return new ApplicationNumber($"APP-{now:yyyyMMdd}-{sequenceValue:D6}");
    }

    /// <summary>Rehydrates an existing number (persistence, lookups).</summary>
    public static ApplicationNumber Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainRuleViolationException("Application number is required.");

        var normalized = value.Trim().ToUpperInvariant();

        if (normalized.Length > MaxLength)
            throw new DomainRuleViolationException($"Application number cannot exceed {MaxLength} characters.");

        return new ApplicationNumber(normalized);
    }

    public override string ToString() => Value;
}