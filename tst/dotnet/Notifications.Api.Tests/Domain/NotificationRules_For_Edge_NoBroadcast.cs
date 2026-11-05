using AwesomeAssertions;

using EnterpriseWebPlatform.Notifications.Api.Domain;
using EnterpriseWebPlatform.Notifications.Api.Tests.Support;

namespace EnterpriseWebPlatform.Notifications.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Need to know: new work reaches only the role's queue in the record's branch, progress
// │ only the initiator. When the branch or the initiator is missing the rules tell nobody
// │ rather than guess a wider audience. A click target carries an application number only
// │ in its exact shape, so an event cannot smuggle a path into the Shell's navigation.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class NotificationRules_For_Edge_NoBroadcast
{
    [Test]
    [Category("Happy")]
    public void Given_ANewKycCase_When_Ruled_Then_OnlyTheKycOfficersOfThatBranchGetNewWork()
    {
        // Given
        var envelope = NotificationEvents.Envelope("KycCaseCreated", new
        {
            KycCaseId = 3, ApplicationNumber = "APP-20261105-000002", CustomerNumber = "CUS-000124", BranchCode = " syd001 ", ApplicantName = "Jason Millers"
        });

        // When
        var draft = NotificationRules.For("KycCaseCreated", envelope).Should().ContainSingle().Subject;

        // Then - the branch normalised, the click opens that case
        draft.Audience.Should().Be("staff:kyc_officer:SYD001");
        draft.Category.Should().Be(NotificationCategory.NewWork);
        draft.Body.Should().Be("Jason Millers · APP-20261105-000002 is waiting for KYC review.");
        NotificationEvents.TargetOf(draft.Target).GetProperty("path").GetString().Should().Be("/v1/kyc/cases/view-details?caseId=3");
    }

    [TestCase("KycCaseCreated")]
    [TestCase("ComplianceCaseScreened")]
    [TestCase("AccountApplicationCreated")]
    [TestCase("PaymentApprovalRequired")]
    public void Given_NewWorkWithoutABranch_When_Ruled_Then_NobodyIsNotified(string eventType)
    {
        // Given
        var envelope = NotificationEvents.Envelope(eventType, new { ApplicationNumber = "APP-20261105-000002", PaymentId = 9, KycCaseId = 3 });

        // When
        var drafts = NotificationRules.For(eventType, envelope);

        // Then
        drafts.Should().BeEmpty();
    }

    [TestCase("KycCaseApproved")]
    [TestCase("ComplianceCaseRejected")]
    [TestCase("AccountOpened")]
    [TestCase("PaymentCompleted")]
    public void Given_ProgressWithoutAnInitiator_When_Ruled_Then_NobodyIsNotified(string eventType)
    {
        // Given
        var envelope = NotificationEvents.Envelope(eventType, new { ApplicationNumber = "APP-20261105-000002", BranchCode = "SYD001" }, initiatorUserId: null);

        // When
        var drafts = NotificationRules.For(eventType, envelope);

        // Then - the branch is known, but progress is never widened to the branch
        drafts.Should().BeEmpty();
    }

    [Test]
    public void Given_AnEventTheRulesDoNotKnow_When_Ruled_Then_NobodyIsNotified()
    {
        // Given
        var envelope = NotificationEvents.Envelope("OnboardingApplicationSubmitted", new { ApplicationNumber = "APP-20261105-000002", BranchCode = "SYD001" });

        // When
        var drafts = NotificationRules.For("OnboardingApplicationSubmitted", envelope);

        // Then
        drafts.Should().BeEmpty();
    }

    [Test]
    public void Given_AnApplicationNumberInItsExactShape_When_Ruled_Then_TheClickOpensThatApplication()
    {
        // Given
        var envelope = NotificationEvents.Envelope("KycCaseApproved", new { ApplicationNumber = "APP-20261105-000001", DecisionByLanId = "etpar" });

        // When
        var draft = NotificationRules.For("KycCaseApproved", envelope).Single();

        // Then
        NotificationEvents.TargetOf(draft.Target).GetProperty("path").GetString()
            .Should().Be("/v1/onboarding/applications/view-all?applicationNumber=APP-20261105-000001");
    }

    [TestCase("APP-20261105-000001&admin=true")]
    [TestCase("../../operations/privacy/erasure")]
    [TestCase("APP-20261105-0000012")]
    [TestCase("app-20261105-000001")]
    public void Given_AnApplicationNumberInAnyOtherShape_When_Ruled_Then_TheClickOpensThePlainList(string applicationNumber)
    {
        // Given
        var envelope = NotificationEvents.Envelope("KycCaseApproved", new { ApplicationNumber = applicationNumber, DecisionByLanId = "etpar" });

        // When
        var draft = NotificationRules.For("KycCaseApproved", envelope).Single();

        // Then
        NotificationEvents.TargetOf(draft.Target).GetProperty("path").GetString().Should().Be("/v1/onboarding/applications/view-all");
    }

    [Test]
    public void Given_ACompensationThatFailed_When_Ruled_Then_TheInitiatorTheBranchOfficersAndTheOperationsDeskAreTold()
    {
        // Given - the one event that needs three audiences: the release must be retried
        var envelope = NotificationEvents.Envelope("PaymentCompensationFailed", new
        {
            PaymentId = 9, PaymentNumber = "PAY-20261105-000009", Amount = 100.00m, PayeeName = "Jane Citizen", BranchCode = "SYD001"
        });

        // When
        var drafts = NotificationRules.For("PaymentCompensationFailed", envelope);

        // Then
        drafts.Select(d => d.Audience).Should().BeEquivalentTo(
            "user:8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a01", "staff:payments_officer:SYD001", "staff:operations_administrator:*");
    }
}