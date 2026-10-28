using AwesomeAssertions;

using EnterpriseWebPlatform.Compliance.Api.Domain.Events;
using EnterpriseWebPlatform.Compliance.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Tests.Support;

namespace EnterpriseWebPlatform.Compliance.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ ABAC on the record: approving needs a clearance at least the screened risk requires
// │ (low 3, medium 4, high 5). Rejecting or holding needs no extra clearance, but always
// │ a written reason; a case on hold must be released before it can be approved.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class ComplianceCase_Approve_Edge_ClearanceForRisk
{
    [TestCase(ScreeningOutcome.Clear, 3)]
    [TestCase(ScreeningOutcome.PotentialMatch, 4)]
    [TestCase(ScreeningOutcome.Match, 5)]
    [Category("Happy")]
    public void Given_AnOfficerWithJustEnoughClearance_When_TheyApprove_Then_TheCaseIsApproved(ScreeningOutcome outcome, int clearance)
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase(outcome);

        // When
        complianceCase.Approve(ComplianceTestData.Olivia, clearance, null, ComplianceTestData.Now);

        // Then
        complianceCase.Status.Should().Be(ComplianceCaseStatus.Approved);
        complianceCase.DecisionByUserId.Should().Be(ComplianceTestData.Olivia);
        complianceCase.DecisionAt.Should().Be(ComplianceTestData.Now);
        complianceCase.DomainEvents.OfType<ComplianceCaseDecidedDomainEvent>().Should().ContainSingle();
    }

    [TestCase(ScreeningOutcome.Clear, 2)]
    [TestCase(ScreeningOutcome.PotentialMatch, 3)]
    [TestCase(ScreeningOutcome.Match, 4)]
    [Category("Boundary")]
    public void Given_AnOfficerOneLevelShort_When_TheyApprove_Then_ItIsRefused(ScreeningOutcome outcome, int clearance)
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase(outcome);

        // When
        var approve = () => complianceCase.Approve(ComplianceTestData.Olivia, clearance, null, ComplianceTestData.Now);

        // Then
        approve.Should().Throw<ClearanceViolationException>().WithMessage($"*requires clearance level {clearance + 1}*");
        complianceCase.Status.Should().Be(ComplianceCaseStatus.UnderReview);
        complianceCase.DecisionByUserId.Should().BeNull();
    }

    [Test]
    public void Given_AHighRiskCase_When_AnOfficerWithoutTheClearanceRejects_Then_TheRejectionStands()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase(ScreeningOutcome.Match);

        // When
        complianceCase.Reject(ComplianceTestData.Olivia, ComplianceTestData.Text("Confirmed sanctions match."), ComplianceTestData.Now);

        // Then
        complianceCase.Status.Should().Be(ComplianceCaseStatus.Rejected);
        complianceCase.DecisionRemarks.Should().Be("Confirmed sanctions match.");
    }

    [Test]
    public void Given_ACaseOnHold_When_TheOfficerApproves_Then_TheHoldMustBeReleasedFirst()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase(ScreeningOutcome.PotentialMatch);
        complianceCase.PlaceOnHold(ComplianceTestData.Olivia, ComplianceTestData.Text(), ComplianceTestData.Now);

        // When
        var approve = () => complianceCase.Approve(ComplianceTestData.Olivia, 5, null, ComplianceTestData.Now);

        // Then
        approve.Should().Throw<DomainConflictException>().WithMessage("*on hold*");
        complianceCase.Status.Should().Be(ComplianceCaseStatus.OnHold);
    }

    [Test]
    public void Given_ACaseOnHold_When_TheOfficerReleasesTheHoldAndApproves_Then_TheCaseIsApproved()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase(ScreeningOutcome.PotentialMatch);
        complianceCase.PlaceOnHold(ComplianceTestData.Olivia, ComplianceTestData.Text(), ComplianceTestData.Now);

        // When
        complianceCase.ReleaseHold(ComplianceTestData.Olivia, ComplianceTestData.Now);
        complianceCase.Approve(ComplianceTestData.Olivia, 4, null, ComplianceTestData.Now);

        // Then
        complianceCase.Status.Should().Be(ComplianceCaseStatus.Approved);
        complianceCase.HoldReason.Should().BeNull();
    }

    [Test]
    public void Given_ACaseOnHold_When_TheOfficerRejectsIt_Then_TheCaseIsRejectedAndTheHoldCleared()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase(ScreeningOutcome.PotentialMatch);
        complianceCase.PlaceOnHold(ComplianceTestData.Olivia, ComplianceTestData.Text(), ComplianceTestData.Now);

        // When
        complianceCase.Reject(ComplianceTestData.Olivia, ComplianceTestData.Text("The customer did not respond."), ComplianceTestData.Now);

        // Then
        complianceCase.Status.Should().Be(ComplianceCaseStatus.Rejected);
        complianceCase.HoldReason.Should().BeNull();
    }

    [Test]
    [Category("Boundary")]
    public void Given_NoReason_When_AnOfficerRejectsOrHolds_Then_ItIsRefused()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();

        // When
        var reject = () => complianceCase.Reject(ComplianceTestData.Olivia, OfficerText.From("  "), ComplianceTestData.Now);
        var hold = () => complianceCase.PlaceOnHold(ComplianceTestData.Olivia, null, ComplianceTestData.Now);

        // Then
        reject.Should().Throw<DomainRuleViolationException>().WithMessage("Remarks are required*");
        hold.Should().Throw<DomainRuleViolationException>().WithMessage("A reason is required*");
        complianceCase.AssignedOfficerUserId.Should().BeNull();
    }

    [Test]
    public void Given_ACaseNotOnHold_When_TheOfficerReleasesAHold_Then_ItIsRefused()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();

        // When
        var release = () => complianceCase.ReleaseHold(ComplianceTestData.Olivia, ComplianceTestData.Now);

        // Then
        release.Should().Throw<DomainConflictException>().WithMessage("*not on hold*");
    }

    [Test]
    public void Given_ADecidedCase_When_AnotherDecisionArrives_Then_ItIsRefused()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();
        complianceCase.Approve(ComplianceTestData.Olivia, 3, null, ComplianceTestData.Now);

        // When
        var reject = () => complianceCase.Reject(ComplianceTestData.Olivia, ComplianceTestData.Text(), ComplianceTestData.Now);

        // Then
        reject.Should().Throw<DomainConflictException>().WithMessage("*already decided*");
        complianceCase.Status.Should().Be(ComplianceCaseStatus.Approved);
    }
}