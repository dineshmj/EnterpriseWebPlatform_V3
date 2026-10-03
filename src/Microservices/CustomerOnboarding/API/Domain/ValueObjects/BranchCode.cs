using System.Text.RegularExpressions;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

/// <summary>An organisational branch (e.g. SYD001): trimmed, upper-case, 1–20 letters/digits.</summary>
public sealed partial record BranchCode
{
    public const int MaxLength = 20;

    private BranchCode(string value) => Value = value;

    public string Value { get; }

    public static BranchCode Create(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(normalized) || !Pattern().IsMatch(normalized))
            throw new DomainRuleViolationException("A valid branch code (1-20 letters or digits) is required.");

        return new BranchCode(normalized);
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Z0-9]{1,20}$")]
    private static partial Regex Pattern();
}