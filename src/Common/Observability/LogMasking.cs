namespace EnterpriseWebPlatform.Common.Observability;

// ┌─ :: SECURITY :: No full account numbers in logs ───────────────────────────────────────────────
// │ Why : Account numbers in logs spread into log shippers and observability platforms with other
// │       access and retention rules.
// │ How : Only the BSB and the last four digits are logged - enough to tell accounts apart; the
// │       payment number and the trace ID find the rest.
// │ Ref : Payments / Accounts README (security rows) | OWASP A09 |
// │       CWE-532 Insertion of Sensitive Information into Log File | PCI DSS 3.4 (masking)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Masks financial identifiers before they reach a log entry. Logs travel to consoles, log
/// shippers and observability platforms with their own access and retention rules, so a full
/// account number never goes there: the BSB and the last four digits are enough to tell
/// accounts apart while investigating; the payment number and the trace ID find the rest.
/// </summary>
public static class LogMasking
{
    /// <summary>"062-000 ****5678" for BSB 062-000, account 12345678.</summary>
    public static string Account(string? bsb, string? accountNumber) =>
        $"{bsb?.Trim()} {AccountNumber(accountNumber)}".Trim();

    /// <summary>"****5678": the last four digits only (all of it masked when shorter than five).</summary>
    public static string AccountNumber(string? accountNumber)
    {
        var value = accountNumber?.Trim() ?? string.Empty;
        return value.Length <= 4 ? new string('*', value.Length) : $"****{value[^4..]}";
    }
}