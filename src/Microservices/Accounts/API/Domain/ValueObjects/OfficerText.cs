using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

/// <summary>
/// Free text an officer writes (decision remarks, hold reason): trimmed, never
/// blank, at most 4000 characters.
/// </summary>
public sealed record OfficerText
{
    public const int MaxLength = 4000;

    private OfficerText(string value) => Value = value;

    public string Value { get; }

    /// <summary>Returns null for missing / blank input; throws when the text is too long.</summary>
    public static OfficerText? From(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.Length > MaxLength)
            throw new DomainRuleViolationException($"The text must not exceed {MaxLength} characters.");

        return new OfficerText(trimmed);
    }

    public override string ToString() => Value;
}