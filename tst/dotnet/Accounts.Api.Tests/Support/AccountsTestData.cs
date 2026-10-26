using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Support;

/// <summary>
/// Known people, branches and accounts for the Accounts domain tests - built through the
/// aggregates' own public methods, as the API builds them.
/// </summary>
internal static class AccountsTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    public const string Sophie = "sub-sophie";   // customer service: started the onboarding
    public const string Olivia = "sub-olivia";   // compliance: approved it
    public const string Jack = "sub-jack";       // account officer
    public const string Ruby = "sub-ruby";       // another account officer

    public const string Customer = "CUST-100001";

    public static readonly BranchCode Sydney = BranchCode.Create("SYD001");
    public static readonly BranchCode Melbourne = BranchCode.Create("MEL001");

    /// <summary>An account application waiting for an account officer.</summary>
    public static AccountApplication PendingApplication(string? initiatedBy = Sophie, string? complianceApprovedBy = Olivia) =>
        AccountApplication.Open(
            Guid.NewGuid(), "APP-261015-TEST01", Customer, complianceCaseId: 5, Sydney,
            HolderName.Create("Ava", "Nguyen"), initiatedBy, complianceApprovedBy, Now);

    /// <summary>Approved by Jack: core banking is opening the account.</summary>
    public static AccountApplication OpeningApplication()
    {
        var application = PendingApplication();
        application.Approve(Jack, AccountProduct.EverydayTransaction, remarks: null, Guid.NewGuid(), Now);
        return application;
    }

    /// <summary>
    /// An open Sydney account of <see cref="Customer"/> with this balance and nothing held: it
    /// opens empty, and Sophie deposits the balance in cash at the counter - as in the API.
    /// </summary>
    public static Account OpenAccount(decimal balance = 5_000m, long id = 11)
    {
        var account = NewAccount(id);
        if (balance > 0)
            Deposit(account, balance);
        return account;
    }

    /// <summary>A newly opened Sydney account of <see cref="Customer"/>: ACTIVE, balance zero.</summary>
    public static Account NewAccount(long id = 11)
    {
        var application = OpeningApplication();
        application.RecordAccountOpened("72947086", "062-000", Now);
        var account = Account.Open("72947086", "062-000", "CB-REF-1", application, Now);
        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ The database assigns the ID (a funds hold names its account by it): set it the way
        // │ EF Core does, through the private setter.
        // └──────────────────────────────────────────────────────────────────────────────────────
        typeof(Account).GetProperty(nameof(Account.Id))!.SetValue(account, id);
        return account;
    }

    /// <summary>Cash Sophie counted at the Sydney counter, credited to <paramref name="account"/>.</summary>
    public static CashDeposit Deposit(
        Account account, decimal amount, BranchCode? staffBranch = null, string customerNumber = Customer, string receivedBy = Sophie) =>
        CashDeposit.Record(Guid.NewGuid(), "DEP-261015-TEST01", account, customerNumber, amount,
            staffBranch ?? Sydney, receivedBy, Now);

    /// <summary>Funds held on <paramref name="account"/> for a payment.</summary>
    public static FundsHold Reserve(Account account, decimal amount, BranchCode? initiatorBranch = null, string customerNumber = Customer) =>
        FundsHold.Reserve(Guid.NewGuid(), "PAY-261015-TEST01", account, customerNumber, amount, "AUD",
            initiatorBranch ?? Sydney, Sophie, Now);
}