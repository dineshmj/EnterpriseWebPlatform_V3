using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

/// <summary>
/// The account holder's name as Compliance cleared it (from compliance.case.approved):
/// the name the account is opened in. A snapshot; Accounts holds no other personal data.
/// </summary>
public sealed class HolderName
{
    public const int MaxLength = 100;

    // For EF Core materialization.
    private HolderName()
    {
        FirstName = null!;
        LastName = null!;
    }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public string FullName => $"{FirstName} {LastName}";

    public static HolderName Create(string? firstName, string? lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            throw new DomainRuleViolationException("The account holder's first and last name are required.");

        if (firstName.Trim().Length > MaxLength || lastName.Trim().Length > MaxLength)
            throw new DomainRuleViolationException($"The account holder's names are limited to {MaxLength} characters.");

        return new HolderName { FirstName = firstName.Trim(), LastName = lastName.Trim() };
    }
}