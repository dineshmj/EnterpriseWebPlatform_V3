using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Kafka delivers at least once and out of order: a reply counts only when it answers the
// │ CURRENT command (its CausationId) at the step that sent it. A late reply to an earlier
// │ attempt, or a duplicate after the saga moved on, is recorded and ignored - it never
// │ moves the payment twice.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class PaymentSaga_Replies_Edge_CausationMatching
{
    [Test]
    public void Given_AReplyToAnEarlierCommand_When_ItArrives_Then_ItIsIgnored()
    {
        // Given: the first ReserveFunds timed out and was sent again.
        var (payment, saga) = PaymentsTestData.Started();
        var first = saga.CurrentCommandId!.Value;
        saga.OnReplyTimeout(payment, PaymentsTestData.Policy, PaymentsTestData.Now.AddSeconds(31));
        var second = saga.CurrentCommandId!.Value;

        // When: the late answer to the FIRST command arrives.
        saga.OnFundsReserved(payment, Guid.NewGuid(), first, PaymentsTestData.Now.AddSeconds(32));

        // Then
        second.Should().NotBe(first);
        saga.Step.Should().Be(SagaStep.ReserveFunds);
        payment.Status.Should().Be(PaymentStatus.ReservingFunds);
        saga.History[^1].Kind.Should().Be("REPLY_IGNORED");
        saga.History[^1].Detail.Should().Contain("earlier command");
    }

    [Test]
    public void Given_TheSagaHasMovedOn_When_ADuplicateReplyArrives_Then_ItIsIgnored()
    {
        // Given
        var (payment, saga) = PaymentsTestData.Started();
        var command = saga.CurrentCommandId!.Value;
        saga.OnFundsReserved(payment, Guid.NewGuid(), command, PaymentsTestData.Now);
        var version = saga.Version;

        // When: Kafka delivers the same reply again.
        saga.OnFundsReserved(payment, Guid.NewGuid(), command, PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.SendToNetwork);
        saga.Version.Should().Be(version);
        saga.History[^1].Kind.Should().Be("REPLY_IGNORED");
        saga.History[^1].Detail.Should().Contain("late or duplicate");
    }

    [Test]
    public void Given_AccountsRefusesTheReservation_When_TheReplyArrives_Then_ThePaymentIsRejectedWithNothingToUndo()
    {
        // Given
        var (payment, saga) = PaymentsTestData.Started();

        // When
        saga.OnFundsReservationFailed(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value,
            "INSUFFICIENT_FUNDS", "Not enough available funds.", PaymentsTestData.Now);

        // Then
        payment.Status.Should().Be(PaymentStatus.Rejected);
        payment.OutcomeCode.Should().Be("INSUFFICIENT_FUNDS");
        saga.Status.Should().Be(SagaStatus.Finished);
        saga.FundsCommands().Should().ContainSingle("no ReleaseFunds: nothing was reserved");
    }

    [Test]
    public void Given_AReleaseWasSentTwice_When_TheAnswerToTheFirstArrives_Then_ItIsAccepted()
    {
        // Given: a refused payment is being compensated; the first ReleaseFunds timed out.
        var (payment, saga) = PaymentsTestData.FundsReserved();
        saga.OnNetworkRefused(payment, "The payee's account is closed.", PaymentsTestData.Policy, PaymentsTestData.Now);
        var firstRelease = saga.CurrentCommandId!.Value;
        saga.OnReplyTimeout(payment, PaymentsTestData.Policy, PaymentsTestData.Now.AddSeconds(31));

        // When: any answer to a release is good news - the funds are free either way.
        saga.OnFundsReleased(payment, Guid.NewGuid(), firstRelease, nothingWasHeld: false, PaymentsTestData.Now.AddSeconds(32));

        // Then
        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.OutcomeCode.Should().Be("NETWORK_REFUSED");
        saga.Status.Should().Be(SagaStatus.Finished);
    }
}