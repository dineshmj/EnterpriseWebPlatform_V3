using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerKyc.Api.Tests.Support;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ KYC obeys an erasure decided in Customer Onboarding: the applicant goes, the case stays
// │ as the record - and nobody can work on a case whose applicant is gone.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class KycCase_ErasePersonalData_Edge_ErasedCase
{
    [Test]
    [Category("Happy")]
    public void Given_ARejectedCase_When_ThePersonalDataIsErased_Then_TheApplicantGoesAndTheOutcomeStays()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.DecideStage(VerificationStageType.IdentityVerification, StageDecision.Reject, KycTestData.Liam, KycTestData.Remarks(), KycTestData.Now);

        // When
        var erased = kycCase.ErasePersonalData(KycTestData.Now.AddDays(1));

        // Then
        erased.Should().BeTrue();
        kycCase.Applicant.Should().BeNull();
        kycCase.IsPersonalDataErased.Should().BeTrue();
        kycCase.PersonalDataErasedAt.Should().Be(KycTestData.Now.AddDays(1));
        kycCase.Status.Should().Be(KycCaseStatus.Rejected);
        kycCase.DecisionByUserId.Should().Be(KycTestData.Liam);
        kycCase.CustomerNumber.Should().Be("CUST-100001");
    }

    [Test]
    public void Given_AnErasedCase_When_TheErasureArrivesAgain_Then_NothingChanges()
    {
        // Given - a redelivered erasure event
        var kycCase = KycTestData.PendingCase();
        kycCase.ErasePersonalData(KycTestData.Now);
        var version = kycCase.Version;

        // When
        var erasedAgain = kycCase.ErasePersonalData(KycTestData.Now.AddHours(1));

        // Then
        erasedAgain.Should().BeFalse();
        kycCase.Version.Should().Be(version);
        kycCase.PersonalDataErasedAt.Should().Be(KycTestData.Now);
    }

    [Test]
    public void Given_AnErasedCase_When_AnOfficerTakesIt_Then_ItIsRefused()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.ErasePersonalData(KycTestData.Now);

        // When
        var take = () => kycCase.Claim(KycTestData.Liam, KycTestData.Now);

        // Then
        take.Should().Throw<DomainConflictException>().WithMessage("*personal data was erased*");
        kycCase.AssignedOfficerUserId.Should().BeNull();
    }

    [Test]
    public void Given_AnErasedCase_When_AnOfficerDecidesAStage_Then_ItIsRefusedAndNothingIsRaised()
    {
        // Given
        var kycCase = KycTestData.PendingCase();
        kycCase.ClearDomainEvents();
        kycCase.ErasePersonalData(KycTestData.Now);

        // When
        var decide = () => kycCase.DecideStage(
            VerificationStageType.DocumentVerification, StageDecision.Approve, KycTestData.Liam, null, KycTestData.Now);

        // Then
        decide.Should().Throw<DomainConflictException>();
        kycCase.DocumentVerification.IsPending.Should().BeTrue();
        kycCase.DomainEvents.Should().BeEmpty();
    }
}