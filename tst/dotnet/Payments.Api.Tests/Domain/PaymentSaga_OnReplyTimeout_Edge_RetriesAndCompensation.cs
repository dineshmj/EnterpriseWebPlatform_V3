using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ When Accounts does not answer: a reservation is asked again a bounded number of times,
// │ then the payment fails and the funds are released; a settlement is NEVER given up (the
// │ network already paid); a release that keeps failing stops the saga as STUCK for
// │ operations to retry - money is never left silently held.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class PaymentSaga_OnReplyTimeout_Edge_RetriesAndCompensation
{
    private static readonly SagaPolicy Policy = PaymentsTestData.Policy;

    [Test]
    public void Given_NoReplyToReserveFunds_When_TheTimeoutPasses_Then_ItIsSentAgainWithALongerWait()
    {
        // Given
        var (payment, saga) = PaymentsTestData.Started();

        // When
        saga.OnReplyTimeout(payment, Policy, PaymentsTestData.Now);

        // Then
        saga.Attempts.Should().Be(2);
        saga.NextCheckAt.Should().Be(PaymentsTestData.Now + TimeSpan.FromSeconds(60));
        saga.FundsCommands().Select(c => c.CommandType).Should().Equal(PaymentSaga.ReserveFundsCommand, PaymentSaga.ReserveFundsCommand);
    }

    [Test]
    [Category("Compensation")]
    public void Given_ReserveFundsUnansweredEveryTime_When_TheLastTimeoutPasses_Then_ThePaymentFailsAndTheFundsAreReleased()
    {
        // Given
        var (payment, saga) = PaymentsTestData.Started();

        // When
        for (var timeout = 1; timeout <= Policy.MaxCommandAttempts; timeout++)
            saga.OnReplyTimeout(payment, Policy, PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.ReleaseFunds);
        saga.FundsCommands()[^1].CommandType.Should().Be(PaymentSaga.ReleaseFundsCommand);
        payment.Status.Should().Be(PaymentStatus.Compensating);
        payment.OutcomeCode.Should().Be("ACCOUNTS_NOT_RESPONDING");
    }

    [Test]
    public void Given_SettleFundsUnansweredManyTimes_When_TheTimeoutsPass_Then_ItIsNeverGivenUp()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved();
        saga.OnNetworkAccepted(payment, "NPP261015ABC", Policy, PaymentsTestData.Now);

        // When
        for (var timeout = 1; timeout <= Policy.MaxCommandAttempts * 3; timeout++)
            saga.OnReplyTimeout(payment, Policy, PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.SettleFunds);
        saga.Status.Should().Be(SagaStatus.Running);
        saga.FundsCommands().Select(c => c.CommandType).Should().NotContain(PaymentSaga.ReleaseFundsCommand);
        saga.NextCheckAt.Should().Be(PaymentsTestData.Now + Policy.MaxReplyTimeout);
    }

    [Test]
    [Category("Compensation")]
    public void Given_ReleaseFundsUnansweredEveryTime_When_TheLastTimeoutPasses_Then_TheSagaIsStuckForOperations()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved();
        saga.OnNetworkRefused(payment, "The payee's account is closed.", Policy, PaymentsTestData.Now);

        // When
        for (var timeout = 1; timeout <= Policy.MaxCommandAttempts; timeout++)
            saga.OnReplyTimeout(payment, Policy, PaymentsTestData.Now);

        // Then
        saga.Status.Should().Be(SagaStatus.Stuck);
        payment.Status.Should().Be(PaymentStatus.CompensationFailed);
        payment.DomainEvents.OfType<PaymentCompensationFailedDomainEvent>().Should().ContainSingle();
        saga.HistoryKinds()[^1].Should().Be("COMPENSATION_FAILED");
    }

    [Test]
    [Category("Compensation")]
    public void Given_AStuckRelease_When_OperationsRetryIt_Then_ANewReleaseIsSentAndItsAnswerEndsThePayment()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved();
        saga.OnNetworkRefused(payment, "The payee's account is closed.", Policy, PaymentsTestData.Now);
        for (var timeout = 1; timeout <= Policy.MaxCommandAttempts; timeout++)
            saga.OnReplyTimeout(payment, Policy, PaymentsTestData.Now);

        // When
        saga.RetryCompensation(payment, "dacoo", Policy, PaymentsTestData.Now);
        saga.OnFundsReleased(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value, nothingWasHeld: false, PaymentsTestData.Now);

        // Then
        payment.Status.Should().Be(PaymentStatus.Failed);
        saga.Status.Should().Be(SagaStatus.Finished);
    }

    [Test]
    public void Given_ARunningSaga_When_OperationsTryToRetryItsCompensation_Then_ItIsRefused()
    {
        // Given
        var (payment, saga) = PaymentsTestData.Started();

        // When
        var retry = () => saga.RetryCompensation(payment, "dacoo", Policy, PaymentsTestData.Now);

        // Then
        retry.Should().Throw<DomainConflictException>().WithMessage("Only a stuck compensation*");
    }
}