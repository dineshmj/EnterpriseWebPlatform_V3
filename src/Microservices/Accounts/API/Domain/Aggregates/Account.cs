using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;

/// <summary>
/// Aggregate root: a customer account, as opened by the core-banking system (which
/// issues the BSB and account number). Simplified: no ledger, interest or statements.
///
/// Invariants: one account per onboarding application (ApplicationRef); a holder
/// (customer number), a product and a core-banking identity are always present.
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

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static Account Open(
        string accountNumber,
        string bsb,
        string coreBankingReference,
        AccountApplication application,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (application.Status != AccountApplicationStatus.Opened || application.Product is null)
            throw new DomainConflictException("An account is opened only for an application the core-banking system has opened.");
        if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(bsb) || string.IsNullOrWhiteSpace(coreBankingReference))
            throw new DomainRuleViolationException("An account needs its account number, BSB and core-banking reference.");

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
            OpenedAt = now,
            UpdatedAt = now,
            Version = 1
        };
    }

    public bool IsInBranch(BranchCode? branch) => branch is not null && BranchCode == branch;
}