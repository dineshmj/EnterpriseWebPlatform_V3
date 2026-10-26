using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Tests.Support;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Cash is credited only to an ACTIVE account of the customer, at the account's OWN
// │ branch, within the PoC's cash limit - and a repeated request under the same key is
// │ recognised only when it asks for exactly the same deposit. A refusal credits nothing.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class CashDeposit_Record_Edge_Rules
{
    [Test]
    [Category("Happy")]
    public void Given_ANewAccount_When_ItOpens_Then_ItsBalanceIsZero()
    {
        // When
        var account = AccountsTestData.NewAccount();

        // Then
        account.Balance.Should().Be(0m);
        account.Available.Should().Be(0m);
    }

    [Test]
    [Category("Happy")]
    public void Given_AnActiveAccount_When_SophieDepositsCash_Then_TheAccountIsCreditedAndTheDepositPublished()
    {
        // Given
        var account = AccountsTestData.NewAccount();

        // When
        var deposit = AccountsTestData.Deposit(account, 5_000m);

        // Then
        account.Balance.Should().Be(5_000m);
        account.Available.Should().Be(5_000m);
        deposit.Amount.Should().Be(5_000m);
        deposit.BalanceAfter.Should().Be(5_000m);
        deposit.AccountId.Should().Be(account.Id);
        deposit.ReceivedByUserId.Should().Be(AccountsTestData.Sophie);
        deposit.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<CashDepositedDomainEvent>();
    }

    [Test]
    [Category("Happy")]
    public void Given_MoneyAlreadyHeldForAPayment_When_CashIsDeposited_Then_OnlyTheBalanceGrows()
    {
        // Given
        var account = AccountsTestData.OpenAccount(1_000m);
        AccountsTestData.Reserve(account, 400m);

        // When
        var deposit = AccountsTestData.Deposit(account, 250m);

        // Then
        account.HeldAmount.Should().Be(400m);
        account.Balance.Should().Be(1_250m);
        account.Available.Should().Be(850m);
        deposit.BalanceAfter.Should().Be(1_250m);
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(10_000)]
    [TestCase(0.001)]
    [Category("Boundary")]
    public void Given_AnAmountOutsideTheCashRule_When_ItIsDeposited_Then_ItIsRefusedAndNothingCredited(decimal amount)
    {
        // Given
        var account = AccountsTestData.NewAccount();

        // When
        var deposit = () => AccountsTestData.Deposit(account, amount);

        // Then
        deposit.Should().Throw<DomainRuleViolationException>().WithMessage("*at most 9,999.99*");
        account.Balance.Should().Be(0m);
    }

    [Test]
    [Category("Boundary")]
    public void Given_TheLargestAllowedAmount_When_ItIsDeposited_Then_ItIsCredited()
    {
        // Given
        var account = AccountsTestData.NewAccount();

        // When
        AccountsTestData.Deposit(account, CashDeposit.MaxAmount);

        // Then
        account.Balance.Should().Be(9_999.99m);
    }

    [Test]
    [Category("Security")]
    public void Given_AStaffMemberAtAnotherBranch_When_TheyDepositCash_Then_ItIsRefused()
    {
        // Given
        var account = AccountsTestData.NewAccount();

        // When
        var deposit = () => AccountsTestData.Deposit(account, 100m, staffBranch: AccountsTestData.Melbourne);

        // Then
        deposit.Should().Throw<DomainConflictException>().WithMessage("*another branch*");
        account.Balance.Should().Be(0m);
    }

    [Test]
    [Category("Security")]
    public void Given_AnotherCustomersNumber_When_CashIsDepositedToThisAccount_Then_ItIsRefused()
    {
        // Given
        var account = AccountsTestData.NewAccount();

        // When
        var deposit = () => AccountsTestData.Deposit(account, 100m, customerNumber: "CUST-100099");

        // Then
        deposit.Should().Throw<DomainConflictException>().WithMessage("*does not belong to this customer*");
        account.Balance.Should().Be(0m);
    }

    [Test]
    [Category("Edge")]
    public void Given_AFrozenAccount_When_CashIsDeposited_Then_ItIsRefused()
    {
        // Given
        var account = AccountsTestData.NewAccount();
        typeof(Account).GetProperty(nameof(Account.Status))!.SetValue(account, AccountStatus.Frozen);

        // When
        var deposit = () => AccountsTestData.Deposit(account, 100m);

        // Then
        deposit.Should().Throw<DomainConflictException>().WithMessage("*ACTIVE account*");
        account.Balance.Should().Be(0m);
    }

    [Test]
    [Category("Edge")]
    public void Given_ARecordedDeposit_When_TheSameRequestArrivesAgain_Then_ItIsRecognisedAsTheSame()
    {
        // Given
        var deposit = AccountsTestData.Deposit(AccountsTestData.NewAccount(), 5_000m);

        // When / Then
        deposit.IsSameRequest(" 062-000", "72947086 ", "cust-100001", 5_000m, AccountsTestData.Sophie).Should().BeTrue();
    }

    [TestCase("062-000", "72947086", 4_000, "sub-sophie", TestName = "Given_ARecordedDeposit_When_TheSameKeyComesWithAnotherAmount_Then_ItIsNotTheSameRequest")]
    [TestCase("062-000", "11111111", 5_000, "sub-sophie", TestName = "Given_ARecordedDeposit_When_TheSameKeyComesForAnotherAccount_Then_ItIsNotTheSameRequest")]
    [TestCase("062-000", "72947086", 5_000, "sub-ethan", TestName = "Given_ARecordedDeposit_When_AnotherPersonReusesTheKey_Then_ItIsNotTheSameRequest")]
    [Category("Security")]
    public void Given_ARecordedDeposit_When_TheSameKeyComesWithOtherDetails_Then_ItIsNotTheSameRequest(
        string bsb, string accountNumber, decimal amount, string receivedBy)
    {
        // Given
        var deposit = AccountsTestData.Deposit(AccountsTestData.NewAccount(), 5_000m);

        // When / Then
        deposit.IsSameRequest(bsb, accountNumber, AccountsTestData.Customer, amount, receivedBy).Should().BeFalse();
    }
}