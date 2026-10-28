using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Events;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerKyc.Api.Tests.Support;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Separation of duties on the KYC case: whoever started the onboarding never takes or
// │ decides it, and an unknown initiator fails closed. Once an officer has the case (by
// │ claiming it or by its first decision), only they decide or release it (ReBAC).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("SoD")]
public sealed class KycCase_DecideStage_Edge_SeparationOfDuties
{
    [TestCase(KycTestData.Sophie)]
    [TestCase("SUB-SOPHIE")]
    [Category("Security")]
    public void Given_TheOnboardingsInitiator_When_TheyDecideAStage_Then_ItIsRefused(string officer)
    {
        // Given
        var kycCase = KycTestData.PendingCase();

        // When
        var decide = () => kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Approve, officer, null, KycTestData.Now);

        // Then
        decide.Should().Throw<SeparationOfDutiesViolationException>().WithMessage("The workflow initiator*");
        kycCase.IdentityVerification.IsPending.Should().BeTrue();
        kycCase.AssignedOfficerUserId.Should().BeNull();
    }

    [Test]
    [Category("Security")]
    public void Given_TheOnboardingsInitiator_When_TheyClaimTheCase_Then_ItIsRefused()
    {
        // Given
        var kycCase = KycTestData.PendingCase();

        // When
        var claim = () => kycCase.Claim(KycTestData.Sophie, KycTestData.Now);

        // Then
        claim.Should().Throw<SeparationOfDutiesViolationException>();
    }

    [TestCase(null)]
    [TestCase("   ")]
    [Category("Security")]
    public void Given_AnUnknownInitiator_When_AnyOfficerDecides_Then_ItIsRefusedFailClosed(string? initiatedBy)
    {
        // Given
        var kycCase = KycTestData.PendingCase(initiatedBy);

        // When
        var decide = () => kycCase.DecideStage(
            VerificationStageType.DocumentVerification, StageDecision.Approve, KycTestData.Liam, null, KycTestData.Now);

        // Then
        decide.Should().Throw<SeparationOfDutiesViolationException>().WithMessage("*cannot be verified*");
    }

    [Test]
    [Category("Happy")]
    public void Given_AnUnassignedCase_When_AnOfficerMakesTheFirstDecision_Then_TheCaseIsAssignedToThem()
    {
        // Given
        var kycCase = KycTestData.PendingCase();

        // When
        kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Approve, KycTestData.Liam, null, KycTestData.Now);

        // Then
        kycCase.AssignedOfficerUserId.Should().Be(KycTestData.Liam);
        kycCase.DomainEvents.OfType<KycCaseAssignedDomainEvent>().Should().ContainSingle()
            .Which.OfficerUserId.Should().Be(KycTestData.Liam);
    }

    [Test]
    [Category("Security")]
    public void Given_ACaseTakenByLiam_When_AnotherOfficerDecides_Then_ItIsRefused()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.Claim(KycTestData.Liam, KycTestData.Now);

        // When
        var decide = () => kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Approve, KycTestData.Noah, null, KycTestData.Now);

        // Then
        decide.Should().Throw<AssignmentViolationException>();
        kycCase.IdentityVerification.IsPending.Should().BeTrue();
    }

    [Test]
    public void Given_ACaseTakenByLiam_When_AnotherOfficerClaimsIt_Then_ItIsRefused()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.Claim(KycTestData.Liam, KycTestData.Now);

        // When
        var claim = () => kycCase.Claim(KycTestData.Noah, KycTestData.Now);

        // Then
        claim.Should().Throw<DomainConflictException>().WithMessage("*another officer*");
    }

    [Test]
    public void Given_ACaseTakenByLiam_When_LiamClaimsItAgain_Then_NothingChanges()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.Claim(KycTestData.Liam, KycTestData.Now);
        var version = kycCase.Version;

        // When
        kycCase.Claim(KycTestData.Liam, KycTestData.Now);

        // Then
        kycCase.Version.Should().Be(version);
    }

    [Test]
    [Category("Security")]
    public void Given_ACaseTakenByLiam_When_AnotherOfficerReleasesIt_Then_ItIsRefused()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.Claim(KycTestData.Liam, KycTestData.Now);

        // When
        var release = () => kycCase.Release(KycTestData.Noah, KycTestData.Now);

        // Then
        release.Should().Throw<AssignmentViolationException>();
        kycCase.AssignedOfficerUserId.Should().Be(KycTestData.Liam);
    }

    [Test]
    public void Given_ACaseReleasedByLiam_When_AnotherOfficerDecides_Then_TheyMay()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.Claim(KycTestData.Liam, KycTestData.Now);
        kycCase.Release(KycTestData.Liam, KycTestData.Now);

        // When
        kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Approve, KycTestData.Noah, null, KycTestData.Now);

        // Then
        kycCase.AssignedOfficerUserId.Should().Be(KycTestData.Noah);
    }

    [Test]
    public void Given_ADecidedCase_When_TheOfficerReleasesOrClaimsIt_Then_ItIsRefused()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Reject, KycTestData.Liam, KycTestData.Remarks(), KycTestData.Now);

        // When
        var release = () => kycCase.Release(KycTestData.Liam, KycTestData.Now);
        var claim = () => kycCase.Claim(KycTestData.Noah, KycTestData.Now);

        // Then
        release.Should().Throw<DomainConflictException>().WithMessage("*terminal state*");
        claim.Should().Throw<DomainConflictException>().WithMessage("*terminal state*");
    }

    [TestCase("")]
    [TestCase("  ")]
    [Category("Boundary")]
    public void Given_NoOfficer_When_ADecisionArrives_Then_ItIsRefused(string officer)
    {
        // Given
        var kycCase = KycTestData.PendingCase();

        // When
        var decide = () => kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Approve, officer, null, KycTestData.Now);

        // Then
        decide.Should().Throw<DomainRuleViolationException>().WithMessage("*must be identified*");
    }
}