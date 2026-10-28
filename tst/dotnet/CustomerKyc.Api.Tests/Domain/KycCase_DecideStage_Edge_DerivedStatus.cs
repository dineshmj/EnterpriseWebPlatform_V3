using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Events;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerKyc.Api.Tests.Support;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The case status is derived from its two stages, in either order: APPROVED only when
// │ both are approved, REJECTED as soon as one is rejected. Each stage is decided once,
// │ and the final decision (with kyc.case.decided) is recorded exactly when it ends.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class KycCase_DecideStage_Edge_DerivedStatus
{
    [TestCase(VerificationStageType.IdentityVerification, VerificationStageType.DocumentVerification)]
    [TestCase(VerificationStageType.DocumentVerification, VerificationStageType.IdentityVerification)]
    [Category("Happy")]
    public void Given_OneStageApproved_When_TheOtherIsApproved_Then_TheCaseIsApproved(
        VerificationStageType first, VerificationStageType second)
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.DecideStage(first, StageDecision.Approve, KycTestData.Liam, null, KycTestData.Now);
        kycCase.Status.Should().Be(KycCaseStatus.PendingReview);
        kycCase.DecisionByUserId.Should().BeNull();

        // When
        kycCase.DecideStage(second, StageDecision.Approve, KycTestData.Liam, null, KycTestData.Now.AddMinutes(5));

        // Then
        kycCase.Status.Should().Be(KycCaseStatus.Approved);
        kycCase.DecisionByUserId.Should().Be(KycTestData.Liam);
        kycCase.DecisionAt.Should().Be(KycTestData.Now.AddMinutes(5));
        kycCase.DomainEvents.OfType<KycCaseDecidedDomainEvent>().Should().ContainSingle();
    }

    [TestCase(VerificationStageType.IdentityVerification)]
    [TestCase(VerificationStageType.DocumentVerification)]
    public void Given_APendingCase_When_OneStageIsRejected_Then_TheCaseIsRejectedAtOnce(VerificationStageType stage)
    {
        // Given
        var kycCase = KycTestData.PendingCase();

        // When
        kycCase.DecideStage(stage, StageDecision.Reject, KycTestData.Liam, KycTestData.Remarks(), KycTestData.Now);

        // Then
        kycCase.Status.Should().Be(KycCaseStatus.Rejected);
        kycCase.IsTerminal.Should().BeTrue();
        kycCase.DecisionRemarks.Should().Be(KycTestData.Remarks().Value);
        kycCase.DomainEvents.OfType<KycCaseDecidedDomainEvent>().Should().ContainSingle();
    }

    [Test]
    public void Given_ARejectedCase_When_TheOtherStageIsDecided_Then_ItIsRefused()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Reject, KycTestData.Liam, KycTestData.Remarks(), KycTestData.Now);

        // When
        var decide = () => kycCase.DecideStage(
            VerificationStageType.DocumentVerification, StageDecision.Approve, KycTestData.Liam, null, KycTestData.Now);

        // Then
        decide.Should().Throw<DomainConflictException>().WithMessage("*terminal state*");
        kycCase.DocumentVerification.IsPending.Should().BeTrue();
    }

    [Test]
    public void Given_AnApprovedStage_When_ItIsDecidedAgain_Then_ItIsRefused()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Approve, KycTestData.Liam, null, KycTestData.Now);

        // When
        var decide = () => kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Reject, KycTestData.Liam, KycTestData.Remarks(), KycTestData.Now);

        // Then
        decide.Should().Throw<DomainConflictException>().WithMessage("*identity verification*no longer awaiting review*");
        kycCase.Status.Should().Be(KycCaseStatus.PendingReview);
    }

    [TestCase(null)]
    [TestCase("   ")]
    [Category("Boundary")]
    public void Given_NoRemarks_When_AStageIsRejected_Then_ItIsRefused(string? remarks)
    {
        // Given
        var kycCase = KycTestData.PendingCase();

        // When
        var reject = () => kycCase.DecideStage(
            VerificationStageType.DocumentVerification, StageDecision.Reject, KycTestData.Liam,
            DecisionRemarks.From(remarks), KycTestData.Now);

        // Then
        reject.Should().Throw<DomainRuleViolationException>().WithMessage("*remarks are required*");
        kycCase.Status.Should().Be(KycCaseStatus.PendingReview);
    }

    [Test]
    [Category("Boundary")]
    public void Given_RemarksAtAndOverTheLimit_When_TheyAreWritten_Then_OnlyTheLimitIsAccepted()
    {
        // Given
        var atLimit = new string('x', DecisionRemarks.MaxLength);
        var overLimit = atLimit + "x";

        // When
        var accepted = DecisionRemarks.From($"  {atLimit}  ");
        var refused = () => DecisionRemarks.From(overLimit);

        // Then
        accepted!.Value.Should().HaveLength(DecisionRemarks.MaxLength);
        refused.Should().Throw<DomainRuleViolationException>();
    }

    [Test]
    public void Given_AStageDecision_When_ItIsRecorded_Then_EveryChangeRaisesTheVersion()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.Version.Should().Be(1);

        // When
        kycCase.DecideStage(
            VerificationStageType.IdentityVerification, StageDecision.Approve, KycTestData.Liam, null, KycTestData.Now);

        // Then
        kycCase.Version.Should().Be(2);
        kycCase.IdentityVerification.DecidedByUserId.Should().Be(KycTestData.Liam);
        kycCase.DomainEvents.OfType<VerificationStageDecidedDomainEvent>().Should().ContainSingle();
    }
}