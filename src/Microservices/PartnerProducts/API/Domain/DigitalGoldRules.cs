using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Domain;

/// <summary>
/// The rules both of the customer's signed documents share - and that the Partner Bank applies
/// again on its side (its Distribution/Instructions.cs): checking them here first means Sophie
/// learns at the counter, with the customer still there, what the Partner Bank would refuse.
/// </summary>
public static partial class DigitalGoldRules
{
    public const decimal MaxPurchaseAmount = 50_000m;
    public const int FormValidDays = 30;
    public const int MinimumAge = 18;

    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    /// <summary>Today in Sydney: the date a transfer slip must carry, and the day forms are dated against.</summary>
    public static DateOnly SydneyToday(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Sydney).DateTime);

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ EWP keeps phone numbers as typed at onboarding ("+61 412 345 678", "0412 345 678");
    // │ the Partner Bank accepts only +614 and 8 digits. Normalised here, once - null when the
    // │ number is not an Australian mobile (the screen then asks for the customer's record to
    // │ be corrected first).
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static string? AustralianMobile(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;
        var digits = NonDigits().Replace(phone, string.Empty);
        if (digits.StartsWith("04", StringComparison.Ordinal) && digits.Length == 10)
            digits = "61" + digits[1..];
        return digits.StartsWith("614", StringComparison.Ordinal) && digits.Length == 11 ? "+" + digits : null;
    }

    public static string DocumentNumber(string? value, string what)
    {
        var number = value?.Trim().ToUpperInvariant();
        return number is not null && DocumentNumberPattern().IsMatch(number)
            ? number
            : throw new DigitalGoldRuleViolationException($"The {what} number must be 4-32 letters, digits or hyphens, as printed on it.");
    }

    public static string ScanSha256(string? value) =>
        value is not null && Sha256Pattern().IsMatch(value)
            ? value
            : throw new DigitalGoldRuleViolationException("The scan's SHA-256 (64 lowercase hex characters, from Documents Management) is required.");

    public static decimal Amount(decimal amount) =>
        amount > 0 && amount <= MaxPurchaseAmount && decimal.Round(amount, 2) == amount
            ? amount
            : throw new DigitalGoldRuleViolationException($"The amount must be more than AUD 0 and at most AUD {MaxPurchaseAmount.ToString("N0", CultureInfo.GetCultureInfo("en-AU"))}, in cents.");

    public static string Bsb(string? value) =>
        value is not null && BsbPattern().IsMatch(value)
            ? value
            : throw new DigitalGoldRuleViolationException("The paying account's BSB (123-456) is required.");

    public static string AccountNumber(string? value) =>
        value is not null && AccountNumberPattern().IsMatch(value)
            ? value
            : throw new DigitalGoldRuleViolationException("The paying account's number (5-9 digits) is required.");

    public static string CustomerNumber(string? value) =>
        value is not null && CustomerNumberPattern().IsMatch(value)
            ? value
            : throw new DigitalGoldRuleViolationException("An EWP customer number (CUST-...) is required.");

    /// <summary>A new business number: prefix, date and 6 random characters (no 0/O/1/I to avoid misreading).</summary>
    public static string NewNumber(string prefix, DateTimeOffset now)
    {
        const string alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < code.Length; i++)
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"{prefix}-{now:yyMMdd}-{new string(code)}";
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigits();

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9-]{3,31}$")]
    private static partial Regex DocumentNumberPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^\d{3}-\d{3}$")]
    private static partial Regex BsbPattern();

    [GeneratedRegex(@"^\d{5,9}$")]
    private static partial Regex AccountNumberPattern();

    [GeneratedRegex(@"^CUST-\d{1,12}$")]
    private static partial Regex CustomerNumberPattern();
}