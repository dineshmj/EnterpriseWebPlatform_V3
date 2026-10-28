using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ An application is submitted once, together with its evidence documents - each one
// │ once. The submitted fact carries them, so KYC opens its case with exactly these.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class OnboardingApplication_Submit_Edge_Evidence
{
    [Test]
    [Category("Happy")]
    public void Given_ADraft_When_ItIsSubmittedWithItsEvidence_Then_KycIsToldWhichDocumentsToReview()
    {
        // Given
        var application = OnboardingTestData.Draft();
        var evidence = OnboardingTestData.Evidence();

        // When
        application.Submit(evidence, OnboardingTestData.Now);

        // Then
        application.Status.Should().Be(OnboardingApplicationStatus.Submitted);
        application.SubmittedAt.Should().Be(OnboardingTestData.Now);
        application.DomainEvents.OfType<OnboardingApplicationSubmittedDomainEvent>().Should().ContainSingle()
            .Which.EvidenceDocuments.Select(d => d.DocumentId).Should().Equal(evidence.Select(d => d.DocumentId));
    }

    [Test]
    [Category("Boundary")]
    public void Given_ADraft_When_ItIsSubmittedWithoutEvidence_Then_ItIsRefused()
    {
        // Given
        var application = OnboardingTestData.Draft();

        // When
        var submit = () => application.Submit([], OnboardingTestData.Now);

        // Then
        submit.Should().Throw<DomainRuleViolationException>().WithMessage("*together with its evidence documents*");
        application.Status.Should().Be(OnboardingApplicationStatus.Draft);
    }

    [Test]
    public void Given_ADraft_When_TheSameDocumentIsSubmittedTwice_Then_ItIsRefused()
    {
        // Given
        var application = OnboardingTestData.Draft();
        var document = Guid.NewGuid();

        // When
        var submit = () => application.Submit(
            [EvidenceDocument.Create(document, "PASSPORT"), EvidenceDocument.Create(document, "TAX_NOTICE")], OnboardingTestData.Now);

        // Then
        submit.Should().Throw<DomainRuleViolationException>().WithMessage("*only once*");
    }

    [Test]
    public void Given_ASubmittedApplication_When_ItIsSubmittedAgain_Then_ItIsRefused()
    {
        // Given
        var application = OnboardingTestData.Submitted();

        // When
        var submit = () => application.Submit(OnboardingTestData.Evidence(), OnboardingTestData.Now);

        // Then
        submit.Should().Throw<DomainRuleViolationException>().WithMessage("*requires status 'DRAFT'*");
        application.EvidenceDocuments.Should().HaveCount(2);
    }

    [TestCase("00000000-0000-0000-0000-000000000000", "PASSPORT")]
    [TestCase("6f1c2d6e-8d1a-4a51-9c55-0a3f7f0d8b11", " ")]
    [Category("Boundary")]
    public void Given_AnIncompleteEvidenceDocument_When_ItIsNamed_Then_ItIsRefused(string documentId, string type)
    {
        // Given / When
        var create = () => EvidenceDocument.Create(Guid.Parse(documentId), type);

        // Then
        create.Should().Throw<DomainRuleViolationException>();
    }

    [Test]
    public void Given_ADraft_When_KycStartsBeforeSubmission_Then_ItIsRefused()
    {
        // Given
        var application = OnboardingTestData.Draft();

        // When
        var start = () => application.StartKyc(OnboardingTestData.Now);
        var recorded = application.RecordKycCaseOpened(OnboardingTestData.Now);

        // Then
        start.Should().Throw<DomainRuleViolationException>().WithMessage("Invalid onboarding application transition*");
        recorded.Should().BeFalse();
    }
}