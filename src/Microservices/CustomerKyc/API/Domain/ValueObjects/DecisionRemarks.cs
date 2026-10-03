using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

/// <summary>An officer's remarks on a decision: trimmed, never blank, at most 4000 characters.</summary>
public sealed record DecisionRemarks
{
    public const int MaxLength = 4000;

    private DecisionRemarks(string value) => Value = value;

    public string Value { get; }

    /// <summary>Returns null for missing / blank input; throws when the text is too long.</summary>
    public static DecisionRemarks? From(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.Length > MaxLength)
            throw new DomainRuleViolationException($"Decision remarks must not exceed {MaxLength} characters.");

        return new DecisionRemarks(trimmed);
    }

    public override string ToString() => Value;
}
