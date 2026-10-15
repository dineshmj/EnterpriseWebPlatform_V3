using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Tests.Support;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Funds are held only for a payment started at the account's OWN branch, for the
// │ account's owner, in its currency, and within what is available. The branch check comes
// │ first: someone at another branch learns nothing else about the account (review R1).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class Account_RefusalToHold_Edge_Reasons
{
    [Test]
    [Category("Happy")]
    public void Given_EverythingInOrder_When_AHoldIsChecked_Then_NothingStandsInTheWay()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m);

        // When
        var refusal = account.RefusalToHold(140m, "AUD", AccountsTestData.Customer, AccountsTestData.Sydney);

        // Then
        refusal.Should().BeNull();
    }

    [Test]
    [Category("Boundary")]
    public void Given_ExactlyTheAvailableAmount_When_AHoldIsChecked_Then_ItIsAllowed()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m);

        // When / Then
        account.RefusalToHold(5_000m, "AUD", AccountsTestData.Customer, AccountsTestData.Sydney).Should().BeNull();
        account.RefusalToHold(5_000.01m, "AUD", AccountsTestData.Customer, AccountsTestData.Sydney)
            .Should().Be(FundsRefusalReason.InsufficientFunds);
    }

    [Test]
    [Category("Security")]
    public void Given_AStaffMemberAtAnotherBranch_When_AHoldIsChecked_Then_ItIsRefusedBeforeAnythingElse()
    {
        // Given: also the wrong customer and too much - the branch is still the only answer.
        var account = AccountsTestData.OpenAccount(5_000m);

        // When
        var refusal = account.RefusalToHold(9_999m, "AUD", "CUST-999999", AccountsTestData.Melbourne);

        // Then
        refusal.Should().Be(FundsRefusalReason.AccountNotInBranch);
    }

    [Test]
    [Category("Security")]
    public void Given_NoBranchForTheInitiator_When_AHoldIsChecked_Then_ItIsRefused()
    {
        // Given
        var account = AccountsTestData.OpenAccount();

        // When / Then
        account.RefusalToHold(10m, "AUD", AccountsTestData.Customer, initiatorBranch: null)
            .Should().Be(FundsRefusalReason.AccountNotInBranch);
    }

    [TestCase("CUST-100002", "AUD", FundsRefusalReason.AccountNotOwned)]
    [TestCase(" cust-100001 ", "NZD", FundsRefusalReason.CurrencyNotSupported)]
    [Category("Boundary")]
    public void Given_AnotherOwnerOrCurrency_When_AHoldIsChecked_Then_ItIsRefusedWithThatReason(string customer, string currency, FundsRefusalReason reason)
    {
        // Given
        var account = AccountsTestData.OpenAccount();

        // When / Then
        account.RefusalToHold(10m, currency, customer, AccountsTestData.Sydney).Should().Be(reason);
    }

    [Test]
    public void Given_FundsAlreadyHeld_When_AnotherHoldIsChecked_Then_OnlyTheRestIsAvailable()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m);
        AccountsTestData.Reserve(account, 4_000m);

        // When / Then
        account.Available.Should().Be(1_000m);
        account.RefusalToHold(1_000.01m, "AUD", AccountsTestData.Customer, AccountsTestData.Sydney)
            .Should().Be(FundsRefusalReason.InsufficientFunds);
    }
}