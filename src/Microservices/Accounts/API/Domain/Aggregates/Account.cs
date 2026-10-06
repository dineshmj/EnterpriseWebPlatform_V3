using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;

/// <summary>
/// Aggregate root: a customer account, as opened by the core-banking system (which
/// issues the BSB and account number). Simplified: no interest or statements; the
/// balance changes only by the demo opening deposit and settled payments.
///
/// Invariants: one account per onboarding application (ApplicationRef); a holder
/// (customer number), a product and a core-banking identity are always present;
/// 0 ≤ held ≤ balance, so the available amount (balance − held) is never negative.
/// </summary>
public sealed class Account
{
    private Account()
    {
        AccountNumber = null!;
        Bsb = null!;
        CustomerNumber = null!;
        HolderName = null!;
        BranchCode = null!;
        CoreBankingReference = null!;
    }

    public long Id { get; private set; }

    public string AccountNumber { get; private set; }

    /// <summary>Bank-State-Branch number (Australia), issued with the account number.</summary>
    public string Bsb { get; private set; }

    /// <summary>The account holder (Customer Onboarding's customer, by value): ReBAC "owns".</summary>
    public string CustomerNumber { get; private set; }

    /// <summary>The name the account is held in.</summary>
    public HolderName HolderName { get; private set; }

    public Guid ApplicationRef { get; private set; }

    public BranchCode BranchCode { get; private set; }

    public AccountProduct Product { get; private set; }

    public AccountStatus Status { get; private set; }

    public string CoreBankingReference { get; private set; }

    /// <summary>ISO 4217 currency of the account. Only AUD accounts are opened.</summary>
    public string Currency { get; private set; } = Money.Aud;

    /// <summary>The ledger balance.</summary>
    public decimal Balance { get; private set; }

    /// <summary>Funds reserved for payments in progress (see FundsHold): not yet debited, no longer available.</summary>
    public decimal HeldAmount { get; private set; }

    /// <summary>What a new payment may use: balance minus the funds already held.</summary>
    public decimal Available => Balance - HeldAmount;

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static Account Open(
        string accountNumber,
        string bsb,
        string coreBankingReference,
        AccountApplication application,
        decimal openingDeposit,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (application.Status != AccountApplicationStatus.Opened || application.Product is null)
            throw new DomainConflictException("An account is opened only for an application the core-banking system has opened.");
        if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(bsb) || string.IsNullOrWhiteSpace(coreBankingReference))
            throw new DomainRuleViolationException("An account needs its account number, BSB and core-banking reference.");
        if (openingDeposit < 0 || decimal.Round(openingDeposit, 2) != openingDeposit)
            throw new DomainRuleViolationException("The opening deposit must be zero or a positive amount in cents.");

        return new Account
        {
            AccountNumber = accountNumber.Trim(),
            Bsb = bsb.Trim(),
            CoreBankingReference = coreBankingReference.Trim(),
            CustomerNumber = application.CustomerNumber,
            HolderName = HolderName.Create(application.HolderName.FirstName, application.HolderName.LastName),
            ApplicationRef = application.ApplicationRef,
            BranchCode = application.BranchCode,
            Product = application.Product.Value,
            Status = AccountStatus.Active,
            Currency = Money.Aud,
            Balance = openingDeposit,
            HeldAmount = 0m,
            OpenedAt = now,
            UpdatedAt = now,
            Version = 1
        };
    }

    public bool IsInBranch(BranchCode? branch) => branch is not null && BranchCode == branch;

    /// <summary>
    /// Why these funds may NOT be held for a payment, or null when they may. Checked and
    /// applied under the account's row lock, so two payments can never both spend the
    /// same money.
    /// </summary>
    public FundsRefusalReason? RefusalToHold(decimal amount, string currency, string customerNumber)
    {
        if (!string.Equals(CustomerNumber, customerNumber?.Trim(), StringComparison.OrdinalIgnoreCase))
            return FundsRefusalReason.AccountNotOwned;
        if (Status != AccountStatus.Active)
            return FundsRefusalReason.AccountNotActive;
        if (!string.Equals(Currency, currency, StringComparison.Ordinal))
            return FundsRefusalReason.CurrencyNotSupported;
        if (Available < amount)
            return FundsRefusalReason.InsufficientFunds;
        return null;
    }

    internal void Hold(decimal amount, DateTimeOffset now)
    {
        if (amount <= 0 || amount > Available)
            throw new DomainConflictException("The account cannot hold these funds.");
        HeldAmount += amount;
        Touch(now);
    }

    internal void ReleaseHeld(decimal amount, DateTimeOffset now)
    {
        if (amount <= 0 || amount > HeldAmount)
            throw new DomainConflictException("The account does not hold these funds.");
        HeldAmount -= amount;
        Touch(now);
    }

    /// <summary>The held funds leave the account: the payment was sent.</summary>
    internal void DebitHeld(decimal amount, DateTimeOffset now)
    {
        if (amount <= 0 || amount > HeldAmount || amount > Balance)
            throw new DomainConflictException("The account does not hold these funds.");
        HeldAmount -= amount;
        Balance -= amount;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}