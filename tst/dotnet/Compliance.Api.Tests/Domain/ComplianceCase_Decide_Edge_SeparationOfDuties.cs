using AwesomeAssertions;

using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Tests.Support;

namespace EnterpriseWebPlatform.Compliance.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Four eyes across contexts: neither the onboarding's initiator nor either KYC officer
// │ who decided the application may take or decide its compliance case; an unknown
// │ initiator fails closed. Once taken, only the assigned officer acts on it (ReBAC).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("SoD")]
public sealed class ComplianceCase_Decide_Edge_SeparationOfDuties
{
    [TestCase(ComplianceTestData.Sophie, "The workflow initiator*")]
    [TestCase("SUB-SOPHIE", "The workflow initiator*")]
    [TestCase(ComplianceTestData.Liam, "*decided this application's KYC*")]
    [TestCase(ComplianceTestData.Noah, "*decided this application's KYC*")]
    [Category("Security")]
    public void Given_SomeoneAlreadyInvolved_When_TheyApprove_Then_ItIsRefused(string officer, string reason)
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();

        // When
        var approve = () => complianceCase.Approve(officer, 5, null, ComplianceTestData.Now);

        // Then
        approve.Should().Throw<SeparationOfDutiesViolationException>().WithMessage(reason);
        complianceCase.AssignedOfficerUserId.Should().BeNull();
    }

    [TestCase(ComplianceTestData.Sophie)]
    [TestCase(ComplianceTestData.Liam)]
    [Category("Security")]
    public void Given_SomeoneAlreadyInvolved_When_TheyClaimTheCase_Then_ItIsRefused(string officer)
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();

        // When
        var claim = () => complianceCase.Claim(officer, ComplianceTestData.Now);

        // Then
        claim.Should().Throw<SeparationOfDutiesViolationException>();
    }

    [Test]
    [Category("Security")]
    public void Given_AnUnknownInitiator_When_AnyOfficerActs_Then_ItIsRefusedFailClosed()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase(initiatedBy: null);

        // When
        var reject = () => complianceCase.Reject(ComplianceTestData.Olivia, ComplianceTestData.Text(), ComplianceTestData.Now);

        // Then
        reject.Should().Throw<SeparationOfDutiesViolationException>().WithMessage("*cannot be verified*");
    }

    [Test]
    [Category("Happy")]
    public void Given_AnUntakenCase_When_AnOfficerHoldsIt_Then_ItIsAssignedToThem()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();

        // When
        complianceCase.PlaceOnHold(ComplianceTestData.Olivia, ComplianceTestData.Text(), ComplianceTestData.Now);

        // Then
        complianceCase.AssignedOfficerUserId.Should().Be(ComplianceTestData.Olivia);
    }

    [Test]
    [Category("Security")]
    public void Given_ACaseTakenByOlivia_When_AnotherOfficerApprovesOrReleasesTheHold_Then_ItIsRefused()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();
        complianceCase.PlaceOnHold(ComplianceTestData.Olivia, ComplianceTestData.Text(), ComplianceTestData.Now);

        // When
        var reject = () => complianceCase.Reject(ComplianceTestData.Ruby, ComplianceTestData.Text(), ComplianceTestData.Now);
        var releaseHold = () => complianceCase.ReleaseHold(ComplianceTestData.Ruby, ComplianceTestData.Now);
        var release = () => complianceCase.Release(ComplianceTestData.Ruby, ComplianceTestData.Now);

        // Then
        reject.Should().Throw<AssignmentViolationException>();
        releaseHold.Should().Throw<AssignmentViolationException>();
        release.Should().Throw<AssignmentViolationException>();
    }

    [Test]
    public void Given_ACaseTakenByOlivia_When_AnotherOfficerClaimsIt_Then_ItIsRefused()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();
        complianceCase.Claim(ComplianceTestData.Olivia, ComplianceTestData.Now);

        // When
        var claim = () => complianceCase.Claim(ComplianceTestData.Ruby, ComplianceTestData.Now);

        // Then
        claim.Should().Throw<DomainConflictException>().WithMessage("*another officer*");
    }

    [Test]
    public void Given_ACaseReleasedByOlivia_When_AnotherOfficerApproves_Then_TheyMay()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();
        complianceCase.Claim(ComplianceTestData.Olivia, ComplianceTestData.Now);
        complianceCase.Release(ComplianceTestData.Olivia, ComplianceTestData.Now);

        // When
        complianceCase.Approve(ComplianceTestData.Ruby, 3, null, ComplianceTestData.Now);

        // Then
        complianceCase.DecisionByUserId.Should().Be(ComplianceTestData.Ruby);
    }

    [Test]
    [Category("Boundary")]
    public void Given_AnOpeningWithoutItsKycCase_When_TheCaseOpens_Then_ItIsRefused()
    {
        // Given / When
        var open = () => ComplianceCase.Open(
            Guid.NewGuid(), "APP-261028-TEST01", "CUST-100001", kycCaseId: 0, ComplianceTestData.Sydney,
            Applicant.Create("Ava", "Nguyen", null, null, null, null, null, null),
            ComplianceTestData.Sophie, ComplianceTestData.Liam, ComplianceTestData.Noah, ComplianceTestData.Now);

        // Then
        open.Should().Throw<DomainRuleViolationException>().WithMessage("*KYC case must be identified*");
    }
}