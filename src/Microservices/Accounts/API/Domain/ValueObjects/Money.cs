using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

/// <summary>Amounts are decimals in the account's currency, in whole cents.</summary>
public static class Money
{
    public const string Aud = "AUD";

    /// <summary>Upper bound of one payment's funds (a sanity limit, not a business tier).</summary>
    public const decimal MaxAmount = 10_000_000m;

    public static decimal ValidAmount(decimal amount)
    {
        if (amount <= 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount)
            throw new DomainRuleViolationException($"The amount must be positive, at most {MaxAmount:N0}, in whole cents.");
        return amount;
    }

    public static string ValidCurrency(string? currency)
    {
        var code = currency?.Trim().ToUpperInvariant();
        if (code is null || code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
            throw new DomainRuleViolationException("A three-letter ISO 4217 currency code is required.");
        return code;
    }
}

/// <summary>The life of a funds hold (one per payment, keyed by the PaymentRef).</summary>
public enum FundsHoldStatus
{
    /// <summary>The funds are reserved: no longer available, not yet debited.</summary>
    Held = 1,

    /// <summary>The hold was not placed (see the refusal reason). Nothing to undo.</summary>
    Refused = 2,

    /// <summary>The held funds were debited: the payment left the account.</summary>
    Settled = 3,

    /// <summary>The hold was undone (compensation), or never existed when the release arrived.</summary>
    Released = 4
}

/// <summary>Why funds could not be held. Published as UPPER_SNAKE_CASE codes.</summary>
public enum FundsRefusalReason
{
    AccountNotFound = 1,
    AccountNotOwned = 2,
    AccountNotActive = 3,
    CurrencyNotSupported = 4,
    InsufficientFunds = 5,

    /// <summary>A release for this payment arrived first: the reservation must not happen any more.</summary>
    AlreadyReleased = 6
}

public static class FundsCodes
{
    public static string ToCode(this FundsHoldStatus status) => status switch
    {
        FundsHoldStatus.Held => "HELD",
        FundsHoldStatus.Refused => "REFUSED",
        FundsHoldStatus.Settled => "SETTLED",
        FundsHoldStatus.Released => "RELEASED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static FundsHoldStatus ParseHoldStatus(string code) => code switch
    {
        "HELD" => FundsHoldStatus.Held,
        "REFUSED" => FundsHoldStatus.Refused,
        "SETTLED" => FundsHoldStatus.Settled,
        "RELEASED" => FundsHoldStatus.Released,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown funds hold status code.")
    };

    public static string ToCode(this FundsRefusalReason reason) => reason switch
    {
        FundsRefusalReason.AccountNotFound => "ACCOUNT_NOT_FOUND",
        FundsRefusalReason.AccountNotOwned => "ACCOUNT_NOT_OWNED",
        FundsRefusalReason.AccountNotActive => "ACCOUNT_NOT_ACTIVE",
        FundsRefusalReason.CurrencyNotSupported => "CURRENCY_NOT_SUPPORTED",
        FundsRefusalReason.InsufficientFunds => "INSUFFICIENT_FUNDS",
        FundsRefusalReason.AlreadyReleased => "ALREADY_RELEASED",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    public static FundsRefusalReason ParseRefusalReason(string code) => code switch
    {
        "ACCOUNT_NOT_FOUND" => FundsRefusalReason.AccountNotFound,
        "ACCOUNT_NOT_OWNED" => FundsRefusalReason.AccountNotOwned,
        "ACCOUNT_NOT_ACTIVE" => FundsRefusalReason.AccountNotActive,
        "CURRENCY_NOT_SUPPORTED" => FundsRefusalReason.CurrencyNotSupported,
        "INSUFFICIENT_FUNDS" => FundsRefusalReason.InsufficientFunds,
        "ALREADY_RELEASED" => FundsRefusalReason.AlreadyReleased,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown funds refusal reason code.")
    };

    /// <summary>A sentence for screens and notifications.</summary>
    public static string Describe(this FundsRefusalReason reason) => reason switch
    {
        FundsRefusalReason.AccountNotFound => "The paying account does not exist.",
        FundsRefusalReason.AccountNotOwned => "The paying account does not belong to this customer.",
        FundsRefusalReason.AccountNotActive => "The paying account is not active.",
        FundsRefusalReason.CurrencyNotSupported => "The paying account is held in another currency.",
        FundsRefusalReason.InsufficientFunds => "Insufficient available funds.",
        FundsRefusalReason.AlreadyReleased => "The payment was already cancelled.",
        _ => reason.ToString()
    };
}