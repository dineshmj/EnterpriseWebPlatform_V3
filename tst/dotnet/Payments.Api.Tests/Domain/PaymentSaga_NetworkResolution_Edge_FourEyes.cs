using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Manual reconciliation of an unconfirmed payment moves money on people's word, so it
// │ needs four eyes: one operator proposes SENT / NOT SENT with evidence, ANOTHER decides;
// │ neither may be the payment's initiator; only after the alert time; and the network's
// │ own answer always wins over an open proposal.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("SoD")]
public sealed class PaymentSaga_NetworkResolution_Edge_FourEyes
{
    private const string Evidence = "NPP statement for today has no entry for this payment.";

    [Test]
    public void Given_ThePaymentIsStillBeingAsked_When_AnOperatorProposesBeforeTheAlertTime_Then_ItIsRefused()
    {
        // Given
        var (_, saga) = PaymentsTestData.ConfirmingWithNetwork();

        // When
        var propose = () => saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, Evidence,
            PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, PaymentsTestData.Now.AddMinutes(1));

        // Then
        propose.Should().Throw<DomainConflictException>().WithMessage("*still being asked*");
    }

    [Test]
    [Category("Security")]
    public void Given_ThePaymentsInitiator_When_TheyProposeAResolution_Then_ItIsRefused()
    {
        // Given
        var (_, saga, later) = PaymentsTestData.UnconfirmedTooLong();

        // When
        var propose = () => saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, Evidence,
            PaymentsTestData.Sophie, "somit", PaymentsTestData.Policy, later);

        // Then
        propose.Should().Throw<SeparationOfDutiesViolationException>();
    }

    [TestCase(null)]
    [TestCase("  ")]
    [Category("Boundary")]
    public void Given_AProposalOfSentWithoutTheNetworksReference_When_ItIsProposed_Then_ItIsRefused(string? reference)
    {
        // Given
        var (_, saga, later) = PaymentsTestData.UnconfirmedTooLong();

        // When
        var propose = () => saga.ProposeNetworkResolution(NetworkOutcome.Sent, reference, Evidence,
            PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // Then
        propose.Should().Throw<DomainRuleViolationException>().WithMessage("*network's reference*");
    }

    [TestCase("too short")]
    [TestCase("")]
    [Category("Boundary")]
    public void Given_EvidenceShorterThanTenCharacters_When_ItIsProposed_Then_ItIsRefused(string evidence)
    {
        // Given
        var (_, saga, later) = PaymentsTestData.UnconfirmedTooLong();

        // When
        var propose = () => saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, evidence,
            PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // Then
        propose.Should().Throw<DomainRuleViolationException>().WithMessage("The evidence must be 10 to 500 characters.");
    }

    [Test]
    [Category("Security")]
    public void Given_AnOpenProposal_When_TheProposerDecidesIt_Then_ItIsRefused()
    {
        // Given
        var (payment, saga, later) = PaymentsTestData.UnconfirmedTooLong();
        saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, Evidence, PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // When
        var decide = () => saga.DecideNetworkResolution(payment, approve: true, PaymentsTestData.Daniel, "dacoo", null,
            PaymentsTestData.Policy, later);

        // Then
        decide.Should().Throw<SeparationOfDutiesViolationException>().WithMessage("Four eyes*");
        saga.ProposedOutcome.Should().Be(NetworkOutcome.NotSent);
    }

    [Test]
    public void Given_AnOpenProposal_When_AnotherOperatorProposes_Then_ItIsRefused()
    {
        // Given
        var (_, saga, later) = PaymentsTestData.UnconfirmedTooLong();
        saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, Evidence, PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // When
        var second = () => saga.ProposeNetworkResolution(NetworkOutcome.Sent, "NPP-1", Evidence,
            PaymentsTestData.Michael, "mitur", PaymentsTestData.Policy, later);

        // Then
        second.Should().Throw<DomainConflictException>().WithMessage("*already waiting*");
    }

    [Test]
    [Category("Compensation")]
    public void Given_NotSentIsProposed_When_ASecondOperatorApproves_Then_TheFundsAreReleased()
    {
        // Given
        var (payment, saga, later) = PaymentsTestData.UnconfirmedTooLong();
        saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, Evidence, PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // When
        saga.DecideNetworkResolution(payment, approve: true, PaymentsTestData.Michael, "mitur", null, PaymentsTestData.Policy, later);

        // Then
        saga.Step.Should().Be(SagaStep.ReleaseFunds);
        saga.FundsCommands()[^1].CommandType.Should().Be(PaymentSaga.ReleaseFundsCommand);
        payment.OutcomeCode.Should().Be("NETWORK_NOT_SENT_CONFIRMED");
        saga.ProposedOutcome.Should().BeNull();
        saga.DomainEvents.OfType<NetworkResolutionDomainEvent>().Select(e => e.Stage)
            .Should().Equal(NetworkResolutionDomainEvent.Proposed, NetworkResolutionDomainEvent.Approved);
    }

    [Test]
    [Category("Happy")]
    public void Given_SentIsProposedWithTheReference_When_ASecondOperatorApproves_Then_TheFundsAreSettled()
    {
        // Given
        var (payment, saga, later) = PaymentsTestData.UnconfirmedTooLong();
        saga.ProposeNetworkResolution(NetworkOutcome.Sent, " NPP-TEST-000145 ", Evidence, PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // When
        saga.DecideNetworkResolution(payment, approve: true, PaymentsTestData.Michael, "mitur", null, PaymentsTestData.Policy, later);

        // Then
        saga.Step.Should().Be(SagaStep.SettleFunds);
        payment.NetworkReference.Should().Be("NPP-TEST-000145");
    }

    [Test]
    public void Given_AnOpenProposal_When_TheSecondOperatorRejectsIt_Then_TheEnquiryGoesOnAndNothingMoves()
    {
        // Given
        var (payment, saga, later) = PaymentsTestData.UnconfirmedTooLong();
        saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, Evidence, PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // When
        saga.DecideNetworkResolution(payment, approve: false, PaymentsTestData.Michael, "mitur",
            "The statement for today is not final yet.", PaymentsTestData.Policy, later);

        // Then
        saga.Step.Should().Be(SagaStep.ConfirmWithNetwork);
        saga.ProposedOutcome.Should().BeNull();
        payment.Status.Should().Be(PaymentStatus.SendingToNetwork);
        saga.FundsCommands().Select(c => c.CommandType).Should().NotContain(PaymentSaga.ReleaseFundsCommand);
    }

    [Test]
    [Category("Boundary")]
    public void Given_AnOpenProposal_When_TheSecondOperatorRejectsWithoutAReason_Then_ItIsRefused()
    {
        // Given
        var (payment, saga, later) = PaymentsTestData.UnconfirmedTooLong();
        saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, Evidence, PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // When
        var reject = () => saga.DecideNetworkResolution(payment, approve: false, PaymentsTestData.Michael, "mitur", null,
            PaymentsTestData.Policy, later);

        // Then
        reject.Should().Throw<DomainRuleViolationException>().WithMessage("The reason for rejecting*");
        saga.ProposedOutcome.Should().Be(NetworkOutcome.NotSent);
    }

    [Test]
    [Category("Resilience")]
    public void Given_AnOpenProposal_When_TheNetworkAnswersItself_Then_TheProposalIsSupersededAndTheNetworkWins()
    {
        // Given: two people think it was NOT sent...
        var (payment, saga, later) = PaymentsTestData.UnconfirmedTooLong();
        saga.ProposeNetworkResolution(NetworkOutcome.NotSent, null, Evidence, PaymentsTestData.Daniel, "dacoo", PaymentsTestData.Policy, later);

        // When: ...but the network, back up, says it was.
        saga.OnNetworkConfirmedSent(payment, "NPP261015REAL", PaymentsTestData.Policy, later);

        // Then
        saga.HistoryKinds().Should().Contain("RESOLUTION_SUPERSEDED");
        saga.ProposedOutcome.Should().BeNull();
        saga.Step.Should().Be(SagaStep.SettleFunds);
        payment.NetworkReference.Should().Be("NPP261015REAL");
    }
}