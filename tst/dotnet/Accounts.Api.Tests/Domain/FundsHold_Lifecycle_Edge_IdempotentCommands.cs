using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Tests.Support;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Accounts is the Payments saga's funds participant. Kafka may deliver a command twice,
// │ so each command is idempotent: a repeated reserve / settle / release answers again
// │ without moving money again. A release that overtakes its reservation blocks the late
// │ reservation. Settled funds can never be released.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class FundsHold_Lifecycle_Edge_IdempotentCommands
{
    [Test]
    [Category("Happy")]
    public void Given_AnOpenAccount_When_FundsAreReserved_Then_TheyAreHeldButNotDebited()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m);

        // When
        var hold = AccountsTestData.Reserve(account, 140m);

        // Then
        hold.Status.Should().Be(FundsHoldStatus.Held);
        hold.AccountId.Should().Be(account.Id);
        account.Balance.Should().Be(5_000m);
        account.HeldAmount.Should().Be(140m);
        account.Available.Should().Be(4_860m);
        hold.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<FundsReservedDomainEvent>();
    }

    [Test]
    public void Given_ARefusal_When_FundsAreReserved_Then_TheAccountIsUntouched()
    {
        // Given
        var account = AccountsTestData.OpenAccount(100m);

        // When
        var hold = AccountsTestData.Reserve(account, 140m);

        // Then
        hold.Status.Should().Be(FundsHoldStatus.Refused);
        hold.RefusalReason.Should().Be(FundsRefusalReason.InsufficientFunds);
        account.HeldAmount.Should().Be(0m);
        hold.DomainEvents.OfType<FundsReservationRefusedDomainEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be(FundsRefusalReason.InsufficientFunds);
    }

    [Test]
    [Category("Happy")]
    public void Given_HeldFunds_When_SettleArrivesTwice_Then_TheAccountIsDebitedOnce()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m);
        var hold = AccountsTestData.Reserve(account, 140m);

        // When
        hold.Settle(account, AccountsTestData.Now);
        hold.Settle(account, AccountsTestData.Now);

        // Then
        hold.Status.Should().Be(FundsHoldStatus.Settled);
        account.Balance.Should().Be(4_860m);
        account.HeldAmount.Should().Be(0m);
        hold.DomainEvents.OfType<FundsSettledDomainEvent>().Should().HaveCount(2, "each command gets its answer");
    }

    [Test]
    [Category("Compensation")]
    public void Given_HeldFunds_When_ReleaseArrivesTwice_Then_TheyAreAvailableAgainOnce()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m);
        var hold = AccountsTestData.Reserve(account, 140m);

        // When
        hold.Release(account, AccountsTestData.Now);
        hold.Release(account, AccountsTestData.Now);

        // Then
        hold.Status.Should().Be(FundsHoldStatus.Released);
        account.Available.Should().Be(5_000m);
        account.Balance.Should().Be(5_000m);
        hold.DomainEvents.OfType<FundsReleasedDomainEvent>().Should().HaveCount(2)
            .And.AllSatisfy(e => e.NothingWasHeld.Should().BeFalse());
    }

    [Test]
    [Category("Compensation")]
    public void Given_ARefusedReservation_When_ReleaseArrives_Then_ItAnswersThatNothingWasHeld()
    {
        // Given
        var account = AccountsTestData.OpenAccount(100m);
        var hold = AccountsTestData.Reserve(account, 140m);

        // When
        hold.Release(account, AccountsTestData.Now);

        // Then
        hold.DomainEvents.OfType<FundsReleasedDomainEvent>().Should().ContainSingle()
            .Which.NothingWasHeld.Should().BeTrue();
        account.HeldAmount.Should().Be(0m);
    }

    [Test]
    public void Given_SettledFunds_When_ReleaseArrives_Then_ItIsRefused()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m);
        var hold = AccountsTestData.Reserve(account, 140m);
        hold.Settle(account, AccountsTestData.Now);

        // When
        var release = () => hold.Release(account, AccountsTestData.Now);

        // Then
        release.Should().Throw<DomainConflictException>().WithMessage("*already debited*");
        account.Balance.Should().Be(4_860m);
    }

    [Test]
    public void Given_ARefusedReservation_When_SettleArrives_Then_ItIsRefused()
    {
        // Given
        var account = AccountsTestData.OpenAccount(100m);
        var hold = AccountsTestData.Reserve(account, 140m);

        // When
        var settle = () => hold.Settle(account, AccountsTestData.Now);

        // Then
        settle.Should().Throw<DomainConflictException>().WithMessage("*nothing to settle*");
    }

    [Test]
    [Category("Security")]
    public void Given_AHoldOnOneAccount_When_ItIsSettledAgainstAnother_Then_ItIsRefused()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m, id: 11);
        var other = AccountsTestData.OpenAccount(5_000m, id: 12);
        var hold = AccountsTestData.Reserve(account, 140m);

        // When
        var settle = () => hold.Settle(other, AccountsTestData.Now);

        // Then
        settle.Should().Throw<DomainConflictException>().WithMessage("*different account*");
        other.Balance.Should().Be(5_000m);
    }

    [Test]
    [Category("Resilience")]
    public void Given_AReleaseThatOvertookItsReservation_When_TheReservationArrivesLate_Then_ItIsRefused()
    {
        // Given: the saga gave up and released before Accounts ever saw the reservation.
        var hold = FundsHold.ReleaseBeforeReservation(Guid.NewGuid(), "PAY-261015-TEST01", "062-000", "72947086",
            AccountsTestData.Customer, "AUD", AccountsTestData.Sophie, AccountsTestData.Now);

        // When
        hold.RepeatReservation(AccountsTestData.Now);

        // Then
        hold.Status.Should().Be(FundsHoldStatus.Released);
        hold.DomainEvents.OfType<FundsReleasedDomainEvent>().Should().ContainSingle().Which.NothingWasHeld.Should().BeTrue();
        hold.DomainEvents.OfType<FundsReservationRefusedDomainEvent>().Should().ContainSingle()
            .Which.Reason.Should().Be(FundsRefusalReason.AlreadyReleased);
    }

    [Test]
    [Category("Resilience")]
    public void Given_HeldFunds_When_TheReservationArrivesAgain_Then_ItIsConfirmedWithoutHoldingMore()
    {
        // Given
        var account = AccountsTestData.OpenAccount(5_000m);
        var hold = AccountsTestData.Reserve(account, 140m);

        // When
        hold.RepeatReservation(AccountsTestData.Now);

        // Then
        account.HeldAmount.Should().Be(140m);
        hold.DomainEvents.OfType<FundsReservedDomainEvent>().Should().HaveCount(2);
    }

    [Test]
    public void Given_AnUnknownAccount_When_FundsAreReserved_Then_TheRefusalSaysSo()
    {
        // Given / When
        var hold = FundsHold.RefuseUnknownAccount(Guid.NewGuid(), "PAY-261015-TEST01", "062-000", "00000000",
            AccountsTestData.Customer, 140m, "AUD", AccountsTestData.Sophie, AccountsTestData.Now);

        // Then
        hold.Status.Should().Be(FundsHoldStatus.Refused);
        hold.RefusalReason.Should().Be(FundsRefusalReason.AccountNotFound);
    }

    [TestCase(0)]
    [TestCase(-5)]
    [TestCase(0.001)]
    [Category("Boundary")]
    public void Given_AnAmountOutsideTheRules_When_FundsAreReserved_Then_ItIsRefused(decimal amount)
    {
        // Given
        var account = AccountsTestData.OpenAccount();

        // When
        var reserve = () => AccountsTestData.Reserve(account, amount);

        // Then
        reserve.Should().Throw<DomainRuleViolationException>();
        account.HeldAmount.Should().Be(0m);
    }
}