using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Domain;

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ The onboarding saga is choreographed: KYC, Compliance and Accounts facts arrive at
// │ least once, on different topics, in any order. A fact that overtakes an earlier one
// │ applies the steps still outstanding; a repeated or stale fact changes nothing. A
// │ rejection names its stage and the evidence documents, so Documents can retain them.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class OnboardingApplication_RecordFacts_Edge_OutOfOrderAndRepeated
{
    [Test]
    [Category("Happy")]
    public void Given_ASubmittedApplication_When_EveryFactArrivesInOrder_Then_ItCompletes()
    {
        // Given
        var application = OnboardingTestData.Submitted();

        // When
        application.RecordKycCaseOpened(OnboardingTestData.Now);
        application.RecordKycApproved(OnboardingTestData.Now);
        application.RecordComplianceCaseOpened(OnboardingTestData.Now);
        application.RecordComplianceApproved(OnboardingTestData.Now);
        application.RecordAccountApplicationCreated(OnboardingTestData.Now);
        application.RecordAccountOpened(OnboardingTestData.Now.AddMinutes(30));

        // Then
        application.Status.Should().Be(OnboardingApplicationStatus.Completed);
        application.CompletedAt.Should().Be(OnboardingTestData.Now.AddMinutes(30));
    }

    [Test]
    public void Given_ASubmittedApplication_When_AccountOpenedOvertakesEveryEarlierFact_Then_ItCompletesThroughEachStep()
    {
        // Given
        var application = OnboardingTestData.Submitted();

        // When
        var changed = application.RecordAccountOpened(OnboardingTestData.Now);

        // Then
        changed.Should().BeTrue();
        application.Status.Should().Be(OnboardingApplicationStatus.Completed);
        application.DomainEvents.OfType<OnboardingApplicationStatusChangedDomainEvent>()
            .Select(e => e.NewStatus)
            .Should().Equal(
                OnboardingApplicationStatus.KycInProgress,
                OnboardingApplicationStatus.KycCompleted,
                OnboardingApplicationStatus.ComplianceInProgress,
                OnboardingApplicationStatus.ComplianceCompleted,
                OnboardingApplicationStatus.AccountOpeningInProgress,
                OnboardingApplicationStatus.Completed);
    }

    [Test]
    public void Given_KycAlreadyApproved_When_TheKycFactsArriveAgain_Then_NothingChanges()
    {
        // Given
        var application = OnboardingTestData.Submitted();
        application.RecordKycApproved(OnboardingTestData.Now);
        var version = application.Version;

        // When
        var opened = application.RecordKycCaseOpened(OnboardingTestData.Now);
        var approved = application.RecordKycApproved(OnboardingTestData.Now);

        // Then
        opened.Should().BeFalse();
        approved.Should().BeFalse();
        application.Version.Should().Be(version);
        application.Status.Should().Be(OnboardingApplicationStatus.KycCompleted);
    }

    [Test]
    public void Given_KycAlreadyApproved_When_AStaleKycRejectionArrives_Then_ItIsIgnored()
    {
        // Given
        var application = OnboardingTestData.Submitted();
        application.RecordKycApproved(OnboardingTestData.Now);

        // When
        var changed = application.RecordKycRejected(OnboardingTestData.Now);

        // Then
        changed.Should().BeFalse();
        application.Status.Should().Be(OnboardingApplicationStatus.KycCompleted);
    }

    [Test]
    [Category("Compensation")]
    public void Given_ASubmittedApplication_When_KycRejectsIt_Then_ItIsRejectedNamingTheStageAndTheEvidence()
    {
        // Given
        var application = OnboardingTestData.Submitted();

        // When
        var changed = application.RecordKycRejected(OnboardingTestData.Now);

        // Then
        changed.Should().BeTrue();
        application.Status.Should().Be(OnboardingApplicationStatus.Rejected);
        var rejected = application.DomainEvents.OfType<OnboardingApplicationRejectedDomainEvent>().Should().ContainSingle().Subject;
        rejected.Stage.Should().Be(OnboardingRejectionStage.Kyc);
        rejected.EvidenceDocuments.Should().HaveCount(2);
    }

    [Test]
    [Category("Compensation")]
    public void Given_KycInProgress_When_TheComplianceRejectionOvertakesKycApproval_Then_ItIsRejectedAtCompliance()
    {
        // Given
        var application = OnboardingTestData.Submitted();
        application.RecordKycCaseOpened(OnboardingTestData.Now);

        // When
        application.RecordComplianceRejected(OnboardingTestData.Now);

        // Then
        application.Status.Should().Be(OnboardingApplicationStatus.Rejected);
        application.DomainEvents.OfType<OnboardingApplicationRejectedDomainEvent>().Single()
            .Stage.Should().Be(OnboardingRejectionStage.Compliance);
    }

    [Test]
    [Category("Compensation")]
    public void Given_AccountOpeningInProgress_When_CoreBankingFails_Then_TheApplicationCompensatesAndIsRejected()
    {
        // Given
        var application = OnboardingTestData.Submitted();
        application.RecordAccountApplicationCreated(OnboardingTestData.Now);

        // When
        application.RecordAccountOpeningFailed(OnboardingTestData.Now);

        // Then
        application.Status.Should().Be(OnboardingApplicationStatus.Rejected);
        var rejected = application.DomainEvents.OfType<OnboardingApplicationRejectedDomainEvent>().Single();
        rejected.Stage.Should().Be(OnboardingRejectionStage.AccountOpening);
        rejected.PreviousStatus.Should().Be(OnboardingApplicationStatus.Compensating);
    }

    [Test]
    public void Given_ARejectedApplication_When_LaterFactsArrive_Then_NothingChanges()
    {
        // Given
        var application = OnboardingTestData.Submitted();
        application.RecordKycRejected(OnboardingTestData.Now);
        var version = application.Version;

        // When
        var opened = application.RecordAccountOpened(OnboardingTestData.Now);
        var rejectedAgain = application.RecordKycRejected(OnboardingTestData.Now);

        // Then
        opened.Should().BeFalse();
        rejectedAgain.Should().BeFalse();
        application.Version.Should().Be(version);
        application.Status.Should().Be(OnboardingApplicationStatus.Rejected);
    }

    [Test]
    public void Given_ACompletedApplication_When_ItIsRejectedOrCancelled_Then_ItIsRefused()
    {
        // Given
        var application = OnboardingTestData.Submitted();
        application.RecordAccountOpened(OnboardingTestData.Now);

        // When
        var reject = () => application.Reject(OnboardingRejectionStage.Accounts, OnboardingTestData.Now);
        var cancel = () => application.Cancel(OnboardingTestData.Now);

        // Then
        reject.Should().Throw<DomainRuleViolationException>().WithMessage("*only be rejected while a decision is pending*");
        cancel.Should().Throw<DomainRuleViolationException>().WithMessage("*terminal status*");
    }

    [Test]
    public void Given_ACompensatingApplication_When_TheCompensationFails_Then_ItEndsCompensationFailed()
    {
        // Given
        var application = OnboardingTestData.Submitted();
        application.StartCompensation(OnboardingTestData.Now);

        // When
        application.MarkCompensationFailed(OnboardingTestData.Now);

        // Then
        application.Status.Should().Be(OnboardingApplicationStatus.CompensationFailed);
        application.IsTerminal.Should().BeTrue();
    }
}