namespace EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

public enum AccountApplicationStatus
{
    /// <summary>Waiting for an account officer's decision.</summary>
    PendingReview = 1,
    OnHold = 2,
    Rejected = 3,

    /// <summary>Approved; the core-banking system is opening (or retrying) the account.</summary>
    Opening = 4,
    Opened = 5,

    /// <summary>Approved, but the core-banking system could not open the account (refused, or kept failing).</summary>
    Failed = 6
}

/// <summary>The product the officer opens for the customer.</summary>
public enum AccountProduct
{
    EverydayTransaction = 1,
    Savings = 2
}

public enum AccountStatus
{
    Active = 1,
    Frozen = 2,
    Closed = 3
}

/// <summary>
/// The persisted / published codes (UPPER_SNAKE_CASE), matching the CHECK constraints
/// in EwpAccountsDb.sql and the Integration Event Catalogue.
/// </summary>
public static class AccountsCodes
{
    public static string ToCode(this AccountApplicationStatus status) => status switch
    {
        AccountApplicationStatus.PendingReview => "PENDING_REVIEW",
        AccountApplicationStatus.OnHold => "ON_HOLD",
        AccountApplicationStatus.Rejected => "REJECTED",
        AccountApplicationStatus.Opening => "OPENING",
        AccountApplicationStatus.Opened => "OPENED",
        AccountApplicationStatus.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static AccountApplicationStatus ParseApplicationStatus(string code) => code switch
    {
        "PENDING_REVIEW" => AccountApplicationStatus.PendingReview,
        "ON_HOLD" => AccountApplicationStatus.OnHold,
        "REJECTED" => AccountApplicationStatus.Rejected,
        "OPENING" => AccountApplicationStatus.Opening,
        "OPENED" => AccountApplicationStatus.Opened,
        "FAILED" => AccountApplicationStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown account application status code.")
    };

    public static bool TryParseApplicationStatus(string? code, out AccountApplicationStatus status)
    {
        try { status = ParseApplicationStatus(code?.Trim().ToUpperInvariant() ?? string.Empty); return true; }
        catch (ArgumentOutOfRangeException) { status = default; return false; }
    }

    public static string ToCode(this AccountProduct product) => product switch
    {
        AccountProduct.EverydayTransaction => "EVERYDAY_TRANSACTION",
        AccountProduct.Savings => "SAVINGS",
        _ => throw new ArgumentOutOfRangeException(nameof(product), product, null)
    };

    public static AccountProduct ParseProduct(string code) => code switch
    {
        "EVERYDAY_TRANSACTION" => AccountProduct.EverydayTransaction,
        "SAVINGS" => AccountProduct.Savings,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown account product code.")
    };

    public static bool TryParseProduct(string? code, out AccountProduct product)
    {
        try { product = ParseProduct(code?.Trim().ToUpperInvariant() ?? string.Empty); return true; }
        catch (ArgumentOutOfRangeException) { product = default; return false; }
    }

    public static string ToCode(this AccountStatus status) => status switch
    {
        AccountStatus.Active => "ACTIVE",
        AccountStatus.Frozen => "FROZEN",
        AccountStatus.Closed => "CLOSED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static AccountStatus ParseAccountStatus(string code) => code switch
    {
        "ACTIVE" => AccountStatus.Active,
        "FROZEN" => AccountStatus.Frozen,
        "CLOSED" => AccountStatus.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown account status code.")
    };
}