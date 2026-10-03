using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

/// <summary>A person's legal name: both parts required, trimmed, at most 100 characters each.</summary>
public sealed record PersonName
{
    public const int MaxPartLength = 100;

    // For EF Core materialization (complex type).
    private PersonName()
    {
        FirstName = null!;
        LastName = null!;
    }

    private PersonName(string firstName, string lastName)
    {
        FirstName = firstName;
        LastName = lastName;
    }

    public string FirstName { get; private init; }

    public string LastName { get; private init; }

    public static PersonName Create(string? firstName, string? lastName) =>
        new(Require(firstName, "First name"), Require(lastName, "Last name"));

    public override string ToString() => $"{FirstName} {LastName}";

    private static string Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainRuleViolationException($"{label} is required.");

        var trimmed = value.Trim();
        if (trimmed.Length > MaxPartLength)
            throw new DomainRuleViolationException($"{label} cannot exceed {MaxPartLength} characters.");

        return trimmed;
    }
}