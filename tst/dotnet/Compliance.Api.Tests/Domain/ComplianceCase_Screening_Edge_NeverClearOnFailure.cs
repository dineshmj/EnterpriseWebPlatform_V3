using AwesomeAssertions;

using EnterpriseWebPlatform.Compliance.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Tests.Support;

namespace EnterpriseWebPlatform.Compliance.Api.Tests.Domain;

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ A screening provider that fails or cannot be reached never counts as "clear": the
// │ case stays in SCREENING, is retried later, and nobody can decide it until a real
// │ verdict arrives. The verdict then sets the risk and the clearance needed to approve.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class ComplianceCase_Screening_Edge_NeverClearOnFailure
{
    [Test]
    public void Given_AScreeningCase_When_TheProviderFails_Then_TheCaseStaysInScreeningAndIsRetried()
    {
        // Given
        var complianceCase = ComplianceTestData.ScreeningCase();
        var retryAt = ComplianceTestData.Now.AddMinutes(2);

        // When
        complianceCase.RecordScreeningFailure("Provider timed out.", retryAt, ComplianceTestData.Now);

        // Then
        complianceCase.Status.Should().Be(ComplianceCaseStatus.Screening);
        complianceCase.ScreeningOutcome.Should().BeNull();
        complianceCase.RiskRating.Should().BeNull();
        complianceCase.ScreeningAttempts.Should().Be(1);
        complianceCase.NextScreeningAt.Should().Be(retryAt);
        complianceCase.LastScreeningError.Should().Be("Provider timed out.");
    }

    [Test]
    [Category("Security")]
    public void Given_AFailedScreening_When_AnOfficerApproves_Then_ItIsRefused()
    {
        // Given
        var complianceCase = ComplianceTestData.ScreeningCase();
        complianceCase.RecordScreeningFailure("Provider unavailable.", ComplianceTestData.Now.AddMinutes(2), ComplianceTestData.Now);

        // When
        var approve = () => complianceCase.Approve(ComplianceTestData.Olivia, officerClearance: 5, null, ComplianceTestData.Now);

        // Then
        approve.Should().Throw<DomainConflictException>().WithMessage("*before its screening has completed*");
        complianceCase.AssignedOfficerUserId.Should().BeNull();
    }

    [Test]
    [Category("Boundary")]
    public void Given_AVeryLongOrEmptyProviderError_When_ItIsRecorded_Then_ItIsKeptShortAndNeverBlank()
    {
        // Given
        var longCase = ComplianceTestData.ScreeningCase();
        var blankCase = ComplianceTestData.ScreeningCase();

        // When
        longCase.RecordScreeningFailure(new string('e', 5000), ComplianceTestData.Now, ComplianceTestData.Now);
        blankCase.RecordScreeningFailure("  ", ComplianceTestData.Now, ComplianceTestData.Now);

        // Then
        longCase.LastScreeningError.Should().HaveLength(2000);
        blankCase.LastScreeningError.Should().Be("Screening failed.");
    }

    [Test]
    public void Given_FailedAttempts_When_TheProviderFinallyAnswers_Then_TheCaseMovesToReviewAndTheErrorIsCleared()
    {
        // Given
        var complianceCase = ComplianceTestData.ScreeningCase();
        complianceCase.RecordScreeningFailure("Provider timed out.", ComplianceTestData.Now.AddMinutes(2), ComplianceTestData.Now);

        // When
        complianceCase.RecordScreeningResult(ScreeningOutcome.Clear, "ScreeningSimulator", "SCR-0001", ComplianceTestData.Now);

        // Then
        complianceCase.Status.Should().Be(ComplianceCaseStatus.UnderReview);
        complianceCase.ScreeningAttempts.Should().Be(2);
        complianceCase.NextScreeningAt.Should().BeNull();
        complianceCase.LastScreeningError.Should().BeNull();
    }

    [TestCase(ScreeningOutcome.Clear, RiskRating.Low, 3)]
    [TestCase(ScreeningOutcome.PotentialMatch, RiskRating.Medium, 4)]
    [TestCase(ScreeningOutcome.Match, RiskRating.High, 5)]
    [Category("Happy")]
    public void Given_AVerdict_When_ItIsRecorded_Then_ItSetsTheRiskAndTheClearanceToApprove(
        ScreeningOutcome outcome, RiskRating risk, int clearance)
    {
        // Given
        var complianceCase = ComplianceTestData.ScreeningCase();

        // When
        complianceCase.RecordScreeningResult(outcome, " ScreeningSimulator ", " SCR-0001 ", ComplianceTestData.Now);

        // Then
        complianceCase.RiskRating.Should().Be(risk);
        complianceCase.RequiredClearance.Should().Be(clearance);
        complianceCase.ScreeningProvider.Should().Be("ScreeningSimulator");
    }

    [Test]
    public void Given_AScreenedCase_When_ASecondResultArrives_Then_ItIsRefused()
    {
        // Given
        var complianceCase = ComplianceTestData.UnderReviewCase();

        // When
        var record = () => complianceCase.RecordScreeningResult(ScreeningOutcome.Match, "ScreeningSimulator", "SCR-0002", ComplianceTestData.Now);
        var fail = () => complianceCase.RecordScreeningFailure("late", ComplianceTestData.Now, ComplianceTestData.Now);

        // Then
        record.Should().Throw<DomainConflictException>();
        fail.Should().Throw<DomainConflictException>();
        complianceCase.RiskRating.Should().Be(RiskRating.Low);
    }

    [TestCase("", "SCR-0001")]
    [TestCase("ScreeningSimulator", " ")]
    [Category("Boundary")]
    public void Given_AResultWithoutProviderOrReference_When_ItIsRecorded_Then_ItIsRefused(string provider, string reference)
    {
        // Given
        var complianceCase = ComplianceTestData.ScreeningCase();

        // When
        var record = () => complianceCase.RecordScreeningResult(ScreeningOutcome.Clear, provider, reference, ComplianceTestData.Now);

        // Then
        record.Should().Throw<DomainRuleViolationException>();
        complianceCase.Status.Should().Be(ComplianceCaseStatus.Screening);
    }
}