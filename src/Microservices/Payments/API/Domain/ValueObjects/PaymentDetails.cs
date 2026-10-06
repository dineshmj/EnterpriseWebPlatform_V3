using System.Text.RegularExpressions;

using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

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

/// <summary>
/// An Australian bank account: BSB (Bank-State-Branch, 6 digits, written 062-000 - the
/// local equivalent of an IFSC or sort code) and an account number of up to 9 digits.
/// </summary>
public sealed partial record BankAccountRef
{
    private BankAccountRef(string bsb, string accountNumber)
    {
        Bsb = bsb;
        AccountNumber = accountNumber;
    }

    public string Bsb { get; }

    public string AccountNumber { get; }

    public static BankAccountRef Create(string? bsb, string? accountNumber, string role)
    {
        var digits = Regex.Replace(bsb ?? string.Empty, "[\\s-]", string.Empty);
        if (!BsbPattern().IsMatch(digits))
            throw new DomainRuleViolationException($"The {role} BSB must be 6 digits (e.g. 062-000).");

        var number = Regex.Replace(accountNumber ?? string.Empty, "\\s", string.Empty);
        if (!AccountNumberPattern().IsMatch(number))
            throw new DomainRuleViolationException($"The {role} account number must be 5 to 9 digits.");

        return new BankAccountRef($"{digits[..3]}-{digits[3..]}", number);
    }

    public override string ToString() => $"{Bsb} {AccountNumber}";

    [GeneratedRegex("^[0-9]{6}$")]
    private static partial Regex BsbPattern();

    [GeneratedRegex("^[0-9]{5,9}$")]
    private static partial Regex AccountNumberPattern();
}

/// <summary>Validation of the remaining payment details.</summary>
public static partial class PaymentRules
{
    public const string Aud = "AUD";
    public const int PayeeNameMaxLength = 140;
    public const int ReferenceMaxLength = 35;

    /// <summary>The assisted channel's per-payment limit (a sanity limit; approval tiers are configuration).</summary>
    public const decimal MaxAmount = 1_000_000m;

    public static decimal ValidAmount(decimal amount)
    {
        if (amount <= 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount)
            throw new DomainRuleViolationException($"The amount must be more than 0 and at most {MaxAmount:N0} AUD, in whole cents.");
        return amount;
    }

    public static string ValidPayeeName(string? name)
    {
        var value = Regex.Replace(name?.Trim() ?? string.Empty, "\\s+", " ");
        if (value.Length is 0 or > PayeeNameMaxLength || !TextPattern().IsMatch(value))
            throw new DomainRuleViolationException($"The payee's account name is required (at most {PayeeNameMaxLength} letters, digits and common punctuation).");
        return value;
    }

    public static string? ValidReference(string? reference)
    {
        var value = Regex.Replace(reference?.Trim() ?? string.Empty, "\\s+", " ");
        if (value.Length == 0)
            return null;
        if (value.Length > ReferenceMaxLength || !TextPattern().IsMatch(value))
            throw new DomainRuleViolationException($"The reference may have at most {ReferenceMaxLength} letters, digits and common punctuation.");
        return value;
    }

    public static string ValidCustomerNumber(string? customerNumber)
    {
        var value = customerNumber?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 100)
            throw new DomainRuleViolationException("The paying customer's number is required.");
        return value;
    }

    // Letters (any script), digits, spaces and the punctuation names and references use.
    [GeneratedRegex(@"^[\p{L}\p{M}0-9 .,'&/()\-#:+]+$")]
    private static partial Regex TextPattern();
}