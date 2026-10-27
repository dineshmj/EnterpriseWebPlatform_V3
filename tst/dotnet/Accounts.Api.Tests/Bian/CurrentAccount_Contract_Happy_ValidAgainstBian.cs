using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Application.Queries;
using EnterpriseWebPlatform.Accounts.Api.Bian;
using EnterpriseWebPlatform.Accounts.Api.Tests.Support;
using EnterpriseWebPlatform.Tests.Bian;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Bian;

// ┌─ :: ARCHITECTURE :: /v2 is BIAN only if BIAN's own file says so ───────────────────────────────
// │ Why : The /v2 contract claims BIAN CurrentAccount (release 14.0.0). A renamed property, a wrong
// │       nesting or a value BIAN does not list would break that claim silently.
// │ How : Every body /v2 sends or accepts is validated against BIAN's published OpenAPI file
// │       (doc/bian, pinned) - the account in each EWP status, a deposit as a partner would send
// │       it, and the recorded deposit.
// │ Ref : doc/bian/README.md | Accounts API README (/v2)
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Happy")]
public sealed class CurrentAccount_Contract_Happy_ValidAgainstBian
{
    private const string BianFile = "CurrentAccount.yaml";

    [TestCase("ACTIVE", "Enabled")]
    [TestCase("FROZEN", "Disabled")]
    [TestCase("CLOSED", "Disabled")]
    public void Given_AnAccountInAnyStatus_When_RetrievedThroughV2_Then_TheBodyIsValidAgainstBiansSchema(string status, string bianStatus)
    {
        // Given
        var account = Account(status);

        // When
        var facility = CurrentAccountMapper.ToFacility(account);

        // Then
        BianSchema.Validate(BianFile, "CurrentAccountFacility", facility, BianJson.Options).Should().BeEmpty();
        facility.AccountStatus.Should().Be(bianStatus);
        facility.AccountDetails!.AccountStatus!.AccountStatus!.StatusReason.Should().Be(status, "EWP's own status travels with BIAN's coarser one");
    }

    [Test]
    public void Given_AnAccountWithFundsOnHold_When_Retrieved_Then_TheCurrentAndAvailableBalancesShowTheHold()
    {
        // Given - 5,000.00 on the account, 1,100.00 held for a payment awaiting approval
        var account = Account("ACTIVE");

        // When
        var facility = CurrentAccountMapper.ToFacility(account);

        // Then
        facility.AccountBalance.Should().Be(new BianAccountBalance { BalanceAmount = Aud("5000.00"), BalanceType = "CurrentBalance" });
        facility.AccountDetails!.AccountBalance.Should().Be(new BianAccountBalance { BalanceAmount = Aud("3900.00"), BalanceType = "AvailableBalance" });
    }

    [Test]
    public void Given_ADepositAsThePartnerWouldSendIt_When_CheckedAgainstBian_Then_ItIsValidAndMapsToACashDeposit()
    {
        // Given
        var request = CurrentAccount_ToCashDeposit_Edge_RequiredFields.CashDeposit();

        // When
        var errors = BianSchema.Validate(BianFile, "Deposit", request, BianJson.Options);
        var (deposit, mappingErrors) = CurrentAccountMapper.ToCashDeposit(request);

        // Then
        errors.Should().BeEmpty();
        mappingErrors.Should().BeEmpty();
        deposit.Should().Be(new BianCashDeposit("CUS-000123", 250.00m));
    }

    [Test]
    public void Given_ARecordedDeposit_When_AnsweredThroughV2_Then_TheBodyIsValidAndCarriesTheBalanceAfter()
    {
        // Given
        var recorded = new CashDepositResult("DEP-20261015-000007", "CUS-000123", "062-000", "72947086", 250m, "AUD", 5250m, AccountsTestData.Now, Created: true);

        // When
        var answer = CurrentAccountMapper.ToDeposit(recorded);

        // Then
        BianSchema.Validate(BianFile, "Deposit", answer, BianJson.Options).Should().BeEmpty();
        answer.DepositTransaction!.TransactionIdentification!.IdentifierValue.Should().Be("DEP-20261015-000007");
        answer.DepositTransaction.FinancialTransactionTargetAccount!.AccountBalance!.BalanceAmount!.AmountValue.Should().Be("5250.00");
    }

    [Test]
    public void Given_ABodyWithABalanceTypeBianDoesNotList_When_Validated_Then_TheCheckFindsIt()
    {
        // Given - the check itself must be able to fail, or a green test proves nothing (two $refs deep)
        var valid = CurrentAccountMapper.ToFacility(Account("ACTIVE"));
        var facility = valid with { AccountDetails = valid.AccountDetails! with { AccountBalance = valid.AccountDetails.AccountBalance! with { BalanceType = "SpendableBalance" } } };

        // When
        var errors = BianSchema.Validate(BianFile, "CurrentAccountFacility", facility, BianJson.Options);

        // Then
        errors.Should().NotBeEmpty();
    }

    private static BianAmount Aud(string value) => new() { AmountValue = value, AmountCurrency = new BianCurrencyCode { Currencycode = "AUD" } };

    private static AccountDetail Account(string status) => new(
        AccountId: 7,
        AccountNumber: "72947086",
        Bsb: "062-000",
        CustomerNumber: "CUS-000123",
        BranchCode: "SYD001",
        Product: "EVERYDAY",
        Status: status,
        CoreBankingReference: "CBS-0000072947086",
        OpenedAt: AccountsTestData.Now,
        HolderName: "Camilla Parker",
        Currency: "AUD",
        Balance: 5000m,
        HeldAmount: 1100m,
        Available: 3900m);
}