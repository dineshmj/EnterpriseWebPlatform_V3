namespace EnterpriseWebPlatform.Common.Observability;

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