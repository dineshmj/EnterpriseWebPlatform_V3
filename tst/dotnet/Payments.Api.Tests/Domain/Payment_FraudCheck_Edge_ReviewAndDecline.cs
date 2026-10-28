using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ What the fraud check's answer does to a payment: REVIEW sends it to a payments officer
// │ even below the tier; DECLINE ends it REJECTED before any funds are reserved (the saga
// │ still records it and publishes PaymentRejected); the fallback can never decline, and a
// │ provider's score must be 0-999.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class Payment_FraudCheck_Edge_ReviewAndDecline
{
    private static FraudAssessment Review => FraudAssessment.FromProvider(FraudDecision.Review, 680, ["NEW_PAYEE", "HIGH_AMOUNT_FOR_NEW_PAYEE"]);

    private static FraudAssessment Decline => FraudAssessment.FromProvider(FraudDecision.Decline, 950, ["PAYEE_ON_MULE_LIST"]);

    [Test]
    public void Given_AReviewBelowTheTier_When_ThePaymentIsInitiated_Then_APaymentsOfficerMustApprove()
    {
        // Given / When
        var payment = PaymentsTestData.NewPayment(140m, fraud: Review);

        // Then
        payment.ApprovalRequired.Should().BeTrue();
        payment.FraudDecision.Should().Be(FraudDecision.Review);
        payment.FraudSource.Should().Be(FraudSource.Provider);
        payment.FraudScore.Should().Be(680);
        payment.FraudReasons.Should().Be("NEW_PAYEE, HIGH_AMOUNT_FOR_NEW_PAYEE");
    }

    [Test]
    public void Given_AReview_When_TheFundsAreReserved_Then_ThePaymentWaitsForAnOfficer()
    {
        // Given
        var payment = PaymentsTestData.NewPayment(140m, fraud: Review);
        var saga = PaymentSaga.Start(payment, PaymentsTestData.Policy, payment.PaymentRef, PaymentsTestData.Now);

        // When
        saga.OnFundsReserved(payment, Guid.NewGuid(), saga.CurrentCommandId!.Value, PaymentsTestData.Now);

        // Then
        payment.Status.Should().Be(PaymentStatus.PendingApproval);
        saga.History[0].Detail.Should().Contain("The fraud check asks for a review");
        saga.History[1].Detail.Should().Be("Fraud check by the payment network's fraud scoring, score 680: REVIEW (NEW_PAYEE, HIGH_AMOUNT_FOR_NEW_PAYEE).");
    }

    [Test]
    public void Given_AnAllowBelowTheTier_When_ThePaymentIsInitiated_Then_NoApprovalIsNeeded()
    {
        // Given / When
        var payment = PaymentsTestData.NewPayment(140m, fraud: FraudAssessment.FromFallback(FraudDecision.Allow, ["PROVIDER_UNAVAILABLE"]));

        // Then
        payment.ApprovalRequired.Should().BeFalse();
        payment.FraudSource.Should().Be(FraudSource.Fallback);
        payment.FraudScore.Should().BeNull();
    }

    [Test]
    public void Given_ADecline_When_TheSagaStarts_Then_ThePaymentIsRejectedAndNoFundsAreReserved()
    {
        // Given
        var payment = PaymentsTestData.NewPayment(140m, fraud: Decline);

        // When
        var saga = PaymentSaga.Start(payment, PaymentsTestData.Policy, payment.PaymentRef, PaymentsTestData.Now);

        // Then: ended at once - nothing asked of Accounts, PaymentRejected published with the reason.
        payment.Status.Should().Be(PaymentStatus.Rejected);
        payment.OutcomeCode.Should().Be(Payment.FraudDeclinedCode);
        payment.EndedAt.Should().Be(PaymentsTestData.Now);
        payment.DomainEvents.OfType<PaymentRejectedDomainEvent>().Should().ContainSingle()
            .Which.ReasonCode.Should().Be("FRAUD_DECLINED");
        saga.FundsCommands().Should().BeEmpty();
        saga.Status.Should().Be(SagaStatus.Finished);
        saga.HistoryKinds().Should().Equal("STARTED", "FRAUD_CHECK", "FINISHED");
    }

    [Test]
    public void Given_TheFallbackRules_When_TheyWouldDecline_Then_ItIsRefused()
    {
        // Given / When
        var decline = () => FraudAssessment.FromFallback(FraudDecision.Decline, ["PROVIDER_UNAVAILABLE"]);

        // Then
        decline.Should().Throw<DomainRuleViolationException>().WithMessage("*only the fraud provider can*");
    }

    [TestCase(-1)]
    [TestCase(1000)]
    [Category("Boundary")]
    public void Given_AScoreOutside0To999_When_TheProvidersAnswerIsKept_Then_ItIsRefused(int score)
    {
        // Given / When
        var keep = () => FraudAssessment.FromProvider(FraudDecision.Allow, score, []);

        // Then
        keep.Should().Throw<DomainRuleViolationException>();
    }

    [Test]
    public void Given_RepeatedBlankAndLongReasons_When_TheAnswerIsKept_Then_TheyAreCleanedAndFitTheColumn()
    {
        // Given
        var reasons = new[] { "NEW_PAYEE", " ", "NEW_PAYEE", new string('X', 300) };

        // When
        var assessment = FraudAssessment.FromProvider(FraudDecision.Allow, 10, reasons);

        // Then
        assessment.Reasons.Should().StartWith("NEW_PAYEE, XXX");
        assessment.Reasons!.Length.Should().Be(200);
        FraudAssessment.FromProvider(FraudDecision.Allow, 10, [" "]).Reasons.Should().BeNull();
    }
}