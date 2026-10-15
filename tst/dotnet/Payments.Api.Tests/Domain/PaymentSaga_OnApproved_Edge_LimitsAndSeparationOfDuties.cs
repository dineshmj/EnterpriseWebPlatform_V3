using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Maker-checker: the person who started a payment never decides it; an officer approves
// │ only up to their clearance's limit; a rejection says why. A rejected payment releases
// │ the reserved funds (compensation) and ends REJECTED.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class PaymentSaga_OnApproved_Edge_LimitsAndSeparationOfDuties
{
    [Test]
    [Category("Happy")]
    public void Given_APaymentAwaitingApproval_When_AnOfficerWithEnoughClearanceApproves_Then_ItGoesToTheNetwork()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved(5_000m);

        // When
        saga.OnApproved(payment, PaymentsTestData.Emily, "emcar", clearance: 3, PaymentsTestData.Limits,
            "Checked with the customer by phone.", Guid.NewGuid(), PaymentsTestData.Now);

        // Then
        saga.Step.Should().Be(SagaStep.SendToNetwork);
        saga.Status.Should().Be(SagaStatus.Running);
        payment.Status.Should().Be(PaymentStatus.SendingToNetwork);
        payment.DecisionByUserId.Should().Be(PaymentsTestData.Emily);
        payment.DecisionRemarks.Should().Be("Checked with the customer by phone.");
    }

    [Test]
    [Category("SoD")]
    [Category("Security")]
    public void Given_ThePaymentsInitiator_When_TheyApproveIt_Then_ItIsRefused()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved(5_000m);

        // When
        var approve = () => saga.OnApproved(payment, PaymentsTestData.Sophie, "somit", clearance: 5, PaymentsTestData.Limits,
            null, Guid.NewGuid(), PaymentsTestData.Now);

        // Then
        approve.Should().Throw<SeparationOfDutiesViolationException>();
        payment.Status.Should().Be(PaymentStatus.PendingApproval);
    }

    [Test]
    [Category("Boundary")]
    [Category("Security")]
    public void Given_AnAmountAboveTheOfficersLimit_When_TheyApprove_Then_ItIsRefusedWithTheirLimit()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved(10_000.01m);

        // When
        var approve = () => saga.OnApproved(payment, PaymentsTestData.Emily, "emcar", clearance: 3, PaymentsTestData.Limits,
            null, Guid.NewGuid(), PaymentsTestData.Now);

        // Then
        approve.Should().Throw<ApprovalLimitExceededException>().WithMessage("*up to 10,000.00 AUD; this one is 10,000.01 AUD*");
        saga.Step.Should().Be(SagaStep.AwaitApproval);
    }

    [Test]
    [Category("Security")]
    public void Given_AnOfficerBelowEveryClearanceLevel_When_TheyApprove_Then_ItIsRefused()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved(1_500m);

        // When
        var approve = () => saga.OnApproved(payment, PaymentsTestData.Emily, "emcar", clearance: 2, PaymentsTestData.Limits,
            null, Guid.NewGuid(), PaymentsTestData.Now);

        // Then
        approve.Should().Throw<ApprovalLimitExceededException>().WithMessage("*does not allow you to approve payments*");
    }

    [Test]
    public void Given_APaymentWithinTheTier_When_SomeoneTriesToApproveIt_Then_ItIsRefused()
    {
        // Given: no approval needed - the saga is already sending it.
        var (payment, saga) = PaymentsTestData.FundsReserved(140m);

        // When
        var approve = () => saga.OnApproved(payment, PaymentsTestData.Emily, "emcar", 3, PaymentsTestData.Limits,
            null, Guid.NewGuid(), PaymentsTestData.Now);

        // Then
        approve.Should().Throw<DomainConflictException>().WithMessage("*not waiting for an approval decision*");
    }

    [Test]
    [Category("Boundary")]
    public void Given_NoRemarks_When_AnOfficerRejects_Then_ItIsRefused()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved(1_500m);

        // When
        var reject = () => saga.OnApprovalRejected(payment, PaymentsTestData.Emily, "emcar", "  ", Guid.NewGuid(),
            PaymentsTestData.Policy, PaymentsTestData.Now);

        // Then
        reject.Should().Throw<DomainRuleViolationException>().WithMessage("*remarks are required*");
        payment.Status.Should().Be(PaymentStatus.PendingApproval);
    }

    [Test]
    [Category("Compensation")]
    public void Given_AnOfficerRejectsWithRemarks_When_AccountsReleasesTheFunds_Then_ThePaymentEndsRejected()
    {
        // Given
        var (payment, saga) = PaymentsTestData.FundsReserved(1_500m);
        saga.OnApprovalRejected(payment, PaymentsTestData.Emily, "emcar", "The customer cancelled by phone.", Guid.NewGuid(),
            PaymentsTestData.Policy, PaymentsTestData.Now);
        var release = saga.FundsCommands()[^1];

        // When
        saga.OnFundsReleased(payment, Guid.NewGuid(), release.MessageId, nothingWasHeld: false, PaymentsTestData.Now);

        // Then
        release.CommandType.Should().Be(PaymentSaga.ReleaseFundsCommand);
        payment.Status.Should().Be(PaymentStatus.Rejected);
        payment.OutcomeCode.Should().Be("APPROVAL_REJECTED");
        payment.DomainEvents.OfType<PaymentRejectedDomainEvent>().Should().ContainSingle();
        saga.Status.Should().Be(SagaStatus.Finished);
    }
}