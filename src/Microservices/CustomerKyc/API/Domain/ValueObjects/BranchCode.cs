using System.Text.RegularExpressions;

using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

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

    public static bool TryCreate(string? value, out BranchCode? branch)
    {
        try { branch = Create(value); return true; }
        catch (DomainRuleViolationException) { branch = null; return false; }
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Z0-9]{1,20}$")]
    private static partial Regex Pattern();
}
