using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ An unanswered send has an UNKNOWN outcome - it may have paid. The saga retries with
// │ doubling waits, then asks the network (status enquiry by Idempotency-Key) and never
// │ releases the funds on time alone; operations are alerted once when it takes too long.
// │ A refusal, by contrast, is a real answer: release at once.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class PaymentSaga_Network_Edge_UnknownOutcome
{
    [Test]
    public void Given_TheNetworkDoesNotAnswer_When_SendsFail_Then_EachWaitDoublesFromFiveSeconds()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved();
        var waits = new List<TimeSpan>();

        // When
        for (var attempt = 1; attempt < PaymentsTestData.Policy.MaxNetworkAttempts; attempt++)
        {
            saga.OnNetworkUnavailable(payment, "503", PaymentsTestData.Policy, PaymentsTestData.Now);
            waits.Add(saga.NextCheckAt!.Value - PaymentsTestData.Now);
        }

        // Then
        waits.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        saga.Step.Should().Be(SagaStep.SendToNetwork);
    }

    [TestCase(1, 5)]
    [TestCase(4, 40)]
    [TestCase(5, 60)]
    [TestCase(30, 60)]
    [Category("Boundary")]
    public void Given_ManyFailedAttempts_When_TheNextWaitIsWorkedOut_Then_ItNeverExceedsTheMaximum(int failedAttempts, int seconds)
    {
        // Given / When
        var wait = PaymentsTestData.Policy.NetworkRetryDelayFor(failedAttempts);

        // Then
        wait.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Test]
    public void Given_TheLastSendFails_When_TheOutcomeIsUnknown_Then_TheSagaAsksTheNetworkAndReleasesNothing()
    {
        // Given / When
        var (payment, saga) = PaymentsTestData.ConfirmingWithNetwork();

        // Then
        saga.Step.Should().Be(SagaStep.ConfirmWithNetwork);
        saga.IsDueForNetworkCheck(PaymentsTestData.Now).Should().BeTrue();
        saga.UnconfirmedSince.Should().Be(PaymentsTestData.Now);
        saga.FundsCommands().Select(c => c.CommandType).Should().NotContain(PaymentSaga.ReleaseFundsCommand);
        payment.Status.Should().Be(PaymentStatus.SendingToNetwork);
    }

    [Test]
    public void Given_TheNetworkConfirmsItSentThePayment_When_TheEnquiryAnswers_Then_TheFundsAreSettledNotReleased()
    {
        // Given
        var (payment, saga) = PaymentsTestData.ConfirmingWithNetwork();

        // When
        saga.OnNetworkConfirmedSent(payment, "NPP261015LOST", PaymentsTestData.Policy, PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.SettleFunds);
        saga.FundsCommands()[^1].CommandType.Should().Be(PaymentSaga.SettleFundsCommand);
        payment.NetworkReference.Should().Be("NPP261015LOST");
    }

    [Test]
    [Category("Compensation")]
    public void Given_TheNetworkNeverReceivedThePayment_When_TheEnquiryAnswers_Then_TheFundsAreReleased()
    {
        // Given
        var (payment, saga) = PaymentsTestData.ConfirmingWithNetwork();

        // When
        saga.OnNetworkConfirmedNotSent(payment, refusalReason: null, PaymentsTestData.Policy, PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.ReleaseFunds);
        saga.FundsCommands()[^1].CommandType.Should().Be(PaymentSaga.ReleaseFundsCommand);
        payment.Status.Should().Be(PaymentStatus.Compensating);
        payment.OutcomeCode.Should().Be("NETWORK_UNAVAILABLE");
    }

    [Test]
    public void Given_TheEnquiryKeepsFailing_When_TheAlertTimePasses_Then_OperationsAreAlertedOnce()
    {
        // Given
        var (_, saga) = PaymentsTestData.ConfirmingWithNetwork();
        var beforeAlert = PaymentsTestData.Now + TimeSpan.FromMinutes(1);
        var afterAlert = PaymentsTestData.Now + TimeSpan.FromMinutes(2);

        // When
        saga.OnNetworkStatusUnknown("503", PaymentsTestData.Policy, beforeAlert);
        saga.OnNetworkStatusUnknown("503", PaymentsTestData.Policy, afterAlert);
        saga.OnNetworkStatusUnknown("503", PaymentsTestData.Policy, afterAlert + TimeSpan.FromMinutes(1));

        // Then
        saga.DomainEvents.OfType<PaymentUnconfirmedByNetworkDomainEvent>().Should().ContainSingle()
            .Which.UnconfirmedSince.Should().Be(PaymentsTestData.Now);
        saga.UnconfirmedAlertedAt.Should().Be(afterAlert);
        saga.HistoryKinds().Count(k => k == "UNCONFIRMED_ALERT").Should().Be(1);
        saga.Step.Should().Be(SagaStep.ConfirmWithNetwork);
    }

    [Test]
    [Category("Compensation")]
    public void Given_TheNetworkRefusesThePayment_When_ItAnswers_Then_TheFundsAreReleasedAndThePaymentFails()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved();

        // When
        saga.OnNetworkRefused(payment, "The payee's account is closed.", PaymentsTestData.Policy, PaymentsTestData.Now);
        saga.OnFundsReleased(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value, nothingWasHeld: false, PaymentsTestData.Now);

        // Then
        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.DomainEvents.OfType<PaymentFailedDomainEvent>().Should().ContainSingle()
            .Which.ReasonCode.Should().Be("NETWORK_REFUSED");
    }

    [Test]
    public void Given_NoNetworkStepIsDue_When_TheStepRunnerClaimsAnAttempt_Then_ItIsRefused()
    {
        // Given: still waiting for Accounts.
        var (_, saga) = PaymentsTestData.Started();

        // When
        var claim = () => saga.ClaimNetworkAttempt(TimeSpan.FromMinutes(1), PaymentsTestData.Now);

        // Then
        claim.Should().Throw<DomainConflictException>().WithMessage("*no network attempt is due*");
    }

    [Test]
    public void Given_ADueSend_When_TheStepRunnerClaimsIt_Then_NoOtherInstanceSeesItDueDuringTheLease()
    {
        // Given
        var (_, saga) = PaymentsTestData.FundsReserved();
        var version = saga.Version;

        // When
        var claimed = saga.ClaimNetworkAttempt(TimeSpan.FromMinutes(1), PaymentsTestData.Now);

        // Then
        claimed.Should().Be(version + 1);
        saga.IsDueForNetwork(PaymentsTestData.Now.AddSeconds(59)).Should().BeFalse();
        saga.IsDueForNetwork(PaymentsTestData.Now.AddMinutes(1)).Should().BeTrue();
    }
}