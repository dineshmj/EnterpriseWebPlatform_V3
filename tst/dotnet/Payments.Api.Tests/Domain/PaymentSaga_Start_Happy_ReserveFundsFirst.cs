using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Orchestrated saga: the first step is always to reserve the funds at Accounts. The
// │ command carries the request as its cause, and the saga waits for a reply to THAT
// │ command (a reply timeout is set).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Happy")]
public sealed class PaymentSaga_Start_Happy_ReserveFundsFirst
{
    [Test]
    public void Given_ASavedPayment_When_TheSagaStarts_Then_ReserveFundsIsSentToAccounts()
    {
        // Given
        var payment = PaymentsTestData.NewPayment(140m);

        // When
        var saga = PaymentSaga.Start(payment, PaymentsTestData.Policy, requestId: payment.PaymentRef, PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.ReserveFunds);
        saga.Status.Should().Be(SagaStatus.Running);
        saga.Attempts.Should().Be(1);
        saga.NextCheckAt.Should().Be(PaymentsTestData.Now + TimeSpan.FromSeconds(30));
        payment.Status.Should().Be(PaymentStatus.ReservingFunds);

        var command = saga.FundsCommands().Should().ContainSingle().Subject;
        command.CommandType.Should().Be(PaymentSaga.ReserveFundsCommand);
        command.MessageId.Should().Be(saga.CurrentCommandId!.Value);
        command.CausationId.Should().Be(payment.PaymentRef);
        command.Amount.Should().Be(140m);
        command.BranchCode.Should().Be("SYD001");
        saga.HistoryKinds().Should().Equal("STARTED", "COMMAND_SENT");
    }

    [Test]
    public void Given_APaymentNotYetSaved_When_TheSagaStarts_Then_ItIsRefused()
    {
        // Given
        var unsaved = Payment.Initiate(
            Guid.NewGuid(), "PAY-261015-TEST04", "CUST-100001", PaymentsTestData.Payer, "Harbour Plumbing", PaymentsTestData.Payee,
            10m, null, PaymentsTestData.Sydney, PaymentsTestData.Sophie, PaymentsTestData.ApprovalThreshold, PaymentsTestData.Now);

        // When
        var start = () => PaymentSaga.Start(unsaved, PaymentsTestData.Policy, Guid.NewGuid(), PaymentsTestData.Now);

        // Then
        start.Should().Throw<DomainConflictException>().WithMessage("*must be saved*");
    }

    [Test]
    public void Given_FundsReservedWithinTheTier_When_TheReplyArrives_Then_ThePaymentGoesToTheNetworkAtOnce()
    {
        // Given
        var (payment, saga) = PaymentsTestData.Started(140m);

        // When
        saga.OnFundsReserved(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value, PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.SendToNetwork);
        saga.IsDueForNetwork(PaymentsTestData.Now).Should().BeTrue();
        payment.Status.Should().Be(PaymentStatus.SendingToNetwork);
        payment.DomainEvents.OfType<PaymentApprovalRequiredDomainEvent>().Should().BeEmpty();
    }

    [Test]
    public void Given_FundsReservedAboveTheTier_When_TheReplyArrives_Then_ThePaymentWaitsForAPaymentsOfficer()
    {
        // Given
        var (payment, saga) = PaymentsTestData.Started(1_200m);

        // When
        saga.OnFundsReserved(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value, PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.AwaitApproval);
        saga.Status.Should().Be(SagaStatus.WaitingForPerson);
        saga.IsDueForNetwork(PaymentsTestData.Now).Should().BeFalse();
        payment.Status.Should().Be(PaymentStatus.PendingApproval);
        payment.DomainEvents.OfType<PaymentApprovalRequiredDomainEvent>().Should().ContainSingle();
    }

    [Test]
    public void Given_TheNetworkAcceptsThePayment_When_AccountsConfirmsTheDebit_Then_ThePaymentIsCompleted()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved();
        saga.OnNetworkAccepted(payment, "NPP261015ABC", PaymentsTestData.Policy, PaymentsTestData.Now);
        var settle = saga.FundsCommands()[^1];

        // When
        saga.OnFundsSettled(payment, Guid.NewGuid(), settle.MessageId, PaymentsTestData.Now);

        // Then
        settle.CommandType.Should().Be(PaymentSaga.SettleFundsCommand);
        payment.Status.Should().Be(PaymentStatus.Completed);
        payment.NetworkReference.Should().Be("NPP261015ABC");
        payment.DomainEvents.OfType<PaymentCompletedDomainEvent>().Should().ContainSingle();
        saga.Status.Should().Be(SagaStatus.Finished);
        saga.Step.Should().Be(SagaStep.Done);
        saga.CurrentCommandId.Should().BeNull();
    }
}