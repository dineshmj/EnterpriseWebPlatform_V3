using System.Globalization;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Application.Queries;

namespace EnterpriseWebPlatform.Accounts.Api.Bian;

/// <summary>A BIAN cash deposit, translated into what EWP's RecordCashDepositCommand needs (the account comes from the path).</summary>
public sealed record BianCashDeposit(string CustomerNumber, decimal Amount);

/// <summary>
/// Translates between BIAN's CurrentAccount contract and EWP's account. Pure: no I/O, so every
/// mapping rule is unit-tested (tst/dotnet/Accounts.Api.Tests/Bian).
/// </summary>
public static class CurrentAccountMapper
{
    private const string Aud = "AUD";

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────
    // │ Where EWP's account sits in BIAN's CurrentAccountFacility (doc/bian/README.md, "Accounts"):
    // │   account number -> CurrentAccountNumber (type AccountNumber); BSB -> the branch's
    // │   identification (a BSB identifies a bank branch); the holder -> the association
    // │   "PartyIsPrimaryOwnerOfAccount" with the customer's name and number; the core-banking
    // │   reference and product -> the product instance. BIAN gives a facility ONE balance and
    // │   its AccountDetails one more: the current balance (funds on hold included) and the
    // │   available balance - the amount on hold is their difference.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public static CurrentAccountFacility ToFacility(AccountDetail account) => new()
    {
        ProductInstanceReference = new BianCurrentAccountAgreement
        {
            ProductAgreementIdentification = new BianIdentifier { IdentifierValue = account.CoreBankingReference },
            AgreementDescription = account.Product
        },
        CurrentAccountNumber = AccountNumber(account.AccountNumber),
        BankBranchLocationReference = new BianBranch { BranchIdentification = new BianIdentifier { IdentifierValue = account.Bsb } },
        AccountCurrency = new BianAccountCurrency { AccountCurrency = new BianCurrencyCode { Currencycode = account.Currency } },
        AssociationType = "PartyIsPrimaryOwnerOfAccount",
        AssociationReference = new BianParty
        {
            PartyName = new BianName { Name = account.HolderName },
            PartyIdentification = new BianPartyIdentification { PartyIdentification = new BianIdentifier { IdentifierValue = account.CustomerNumber } }
        },
        AccountDetails = new BianAccount
        {
            AccountStatus = new BianAccountStatus
            {
                AccountStatus = new BianStatus { StatusReason = account.Status },
                AccountStatusType = ToBianStatusType(account.Status)
            },
            AccountIdentification = AccountNumber(account.AccountNumber),
            AccountBalance = new BianAccountBalance { BalanceAmount = Amount(account.Available, account.Currency), BalanceType = "AvailableBalance" }
        },
        AccountDateType = "OpeningDate",
        AccountDate = new BianDatetime { DateTimeContent = account.OpenedAt.ToString("o", CultureInfo.InvariantCulture) },
        AccountBalance = new BianAccountBalance { BalanceAmount = Amount(account.Balance, account.Currency), BalanceType = "CurrentBalance" },
        AccountStatus = ToBianStatusType(account.Status)
    };

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────
    // │ BIAN has Enabled / Disabled / Deleted / Proforma / Pending; "Disabled" is defined as
    // │ "cannot be used, temporarily or permanently" - EWP's FROZEN and CLOSED both. EWP's own
    // │ code travels in AccountDetails.AccountStatus.AccountStatus.StatusReason.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public static string ToBianStatusType(string ewpStatus) => ewpStatus switch
    {
        "ACTIVE" => "Enabled",
        _ => "Disabled"
    };

    /// <summary>
    /// A cash deposit at the counter. SEC: BIAN makes every property optional; EWP requires the
    /// depositing customer (DepositTransactionSourceReference - checked against the account's
    /// owner, as /v1 checks the customer number) and a positive AUD amount; a deposit type other
    /// than CashDeposit is refused, so a cheque or card deposit is never booked as cash.
    /// </summary>
    public static (BianCashDeposit? Deposit, IReadOnlyList<string> Errors) ToCashDeposit(Deposit? request)
    {
        if (request is null)
            return (null, ["A Deposit body is required."]);

        var errors = new List<string>();
        if (request.DepositType is { } type && !string.Equals(type, "CashDeposit", StringComparison.Ordinal))
            errors.Add($"Only DepositType 'CashDeposit' is supported here ('{type}').");

        var customer = request.DepositTransactionSourceReference?.Trim();
        if (string.IsNullOrEmpty(customer))
            errors.Add("DepositTransactionSourceReference (the depositing customer's number) is required.");

        var amountText = request.DepositTransactionAmount?.AmountValue;
        decimal amount = 0;
        if (string.IsNullOrWhiteSpace(amountText))
            errors.Add("DepositTransactionAmount.AmountValue is required.");
        else if (!decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) || amount <= 0)
            errors.Add("DepositTransactionAmount.AmountValue must be a positive decimal number with a '.' (e.g. 250.00).");

        var currency = request.DepositTransactionAmount?.AmountCurrency?.Currencycode;
        if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(currency.Trim(), Aud, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Only {Aud} deposits are supported (currency '{currency.Trim()}').");

        return errors.Count > 0 ? (null, errors) : (new BianCashDeposit(customer!, amount), errors);
    }

    /// <summary>The recorded deposit: its number, the amount, the date, and the account's balance after it.</summary>
    public static Deposit ToDeposit(CashDepositResult deposit) => new()
    {
        DepositType = "CashDeposit",
        DepositTransaction = new BianDepositTransaction
        {
            DepositAmount = Amount(deposit.Amount, deposit.Currency),
            TransactionIdentification = new BianIdentifier { IdentifierValue = deposit.DepositNumber },
            FinancialTransactionTargetAccount = new BianAccount
            {
                AccountIdentification = AccountNumber(deposit.AccountNumber),
                AccountBalance = new BianAccountBalance { BalanceAmount = Amount(deposit.BalanceAfter, deposit.Currency), BalanceType = "CurrentBalance" }
            }
        },
        DepositTransactionSourceReference = deposit.CustomerNumber,
        DepositTransactionAmount = Amount(deposit.Amount, deposit.Currency),
        DepositTransactionDate = new BianDate { DateContent = deposit.RecordedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
    };

    private static BianAccountIdentification AccountNumber(string accountNumber) => new()
    {
        AccountIdentificationType = "AccountNumber",
        AccountIdentification = new BianIdentifier { IdentifierValue = accountNumber }
    };

    private static BianAmount Amount(decimal amount, string currency) => new()
    {
        AmountValue = amount.ToString("0.00", CultureInfo.InvariantCulture),
        AmountCurrency = new BianCurrencyCode { Currencycode = currency }
    };
}