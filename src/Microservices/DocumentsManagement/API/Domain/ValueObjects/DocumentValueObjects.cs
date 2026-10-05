using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;

namespace EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

/// <summary>
/// The organisational branch a document belongs to (e.g. SYD001): trimmed, upper-case,
/// 1–20 letters/digits. The unit of object-level (branch-scoped) authorization.
/// </summary>
public sealed partial record BranchCode
{
    private BranchCode(string value) => Value = value;

    public string Value { get; }

    public static BranchCode Create(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized) || !Pattern().IsMatch(normalized))
            throw new DomainRuleViolationException("A valid branch code (1-20 letters or digits) is required.");

        return new BranchCode(normalized);
    }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out BranchCode? branch)
    {
        try { branch = Create(value); return true; }
        catch (DomainRuleViolationException) { branch = null; return false; }
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[A-Z0-9]{1,20}$")]
    private static partial Regex Pattern();
}

/// <summary>
/// A safe display / download file name: no path, no control or reserved characters,
/// at most 255 characters. Never used to locate the stored content.
/// </summary>
public sealed record FileName
{
    public const int MaxLength = 255;

    private FileName(string value) => Value = value;

    public string Value { get; }

    public static FileName Create(string? value)
    {
        var name = Path.GetFileName(value ?? string.Empty);
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or '*' or '?' or ':')).ToArray()).Trim();

        if (string.IsNullOrEmpty(cleaned) || cleaned is "." or "..")
            throw new DomainRuleViolationException("A valid file name is required.");

        if (cleaned.Length > MaxLength)
            throw new DomainRuleViolationException($"The file name cannot exceed {MaxLength} characters.");

        return new FileName(cleaned);
    }

    public override string ToString() => Value;
}

/// <summary>SHA-256 of the stored content: 64 lower-case hexadecimal characters.</summary>
public sealed partial record ContentHash
{
    private ContentHash(string value) => Value = value;

    public string Value { get; }

    public static ContentHash Create(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || !Pattern().IsMatch(normalized))
            throw new DomainRuleViolationException("A content hash must be a SHA-256 value (64 hexadecimal characters).");

        return new ContentHash(normalized);
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Pattern();
}