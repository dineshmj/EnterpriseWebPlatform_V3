using System.Text.Json;

using AwesomeAssertions;

using EnterpriseWebPlatform.Notifications.Api.Domain;
using EnterpriseWebPlatform.Notifications.Api.Tests.Support;

namespace EnterpriseWebPlatform.Notifications.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Four eyes on an erasure: the request is new work for the WHOLE operations desk (one
// │ of them, never the requester, decides) and the decision goes back to the requester
// │ only. The notifications name the customer number, the reason and LAN IDs - never the
// │ person whose data is erased, even if a producer put a name into the event.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class NotificationRules_For_Edge_PersonalDataErasure
{
    private const string OperationsDesk = "staff:operations_administrator:*";
    private const string ErasurePage = "/v1/operations/privacy/erasure";

    private static object Request(string reasonCode = "CUSTOMER_REQUEST") =>
        new { ErasureRequestId = 7, CustomerId = 42, CustomerNumber = "CUS-000123", ReasonCode = reasonCode, Status = "PENDING" };

    private static object Decision(string status) =>
        new
        {
            ErasureRequestId = 7, CustomerId = 42, CustomerNumber = "CUS-000123", ReasonCode = "CUSTOMER_REQUEST", Status = status,
            DecisionByUserId = "8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a02", DecisionByLanId = "mitur"
        };

    [Test]
    [Category("Happy")]
    public void Given_AnErasureRequest_When_Ruled_Then_EveryOperationsAdministratorGetsNewWorkNamingTheRequester()
    {
        // Given
        var envelope = NotificationEvents.Envelope("PersonalDataErasureRequested", Request());

        // When
        var drafts = NotificationRules.For("PersonalDataErasureRequested", envelope);

        // Then - one draft, for the desk in every branch; nothing addressed to the requester
        var draft = drafts.Should().ContainSingle().Subject;
        draft.Audience.Should().Be(OperationsDesk);
        draft.Category.Should().Be(NotificationCategory.NewWork);
        draft.Title.Should().Be("Erasure to approve");
        draft.Body.Should().Be("dacoo asks to erase the personal data of CUS-000123 (the customer asked to be forgotten). Another operator must approve or reject it.");
    }

    [Test]
    public void Given_ARequestBecauseTheRetentionEnded_When_Ruled_Then_TheBodyGivesThatReason()
    {
        // Given
        var envelope = NotificationEvents.Envelope("PersonalDataErasureRequested", Request("RETENTION_ENDED"));

        // When
        var draft = NotificationRules.For("PersonalDataErasureRequested", envelope).Single();

        // Then
        draft.Body.Should().Contain("(the retention period has ended)");
    }

    [Test]
    public void Given_ARequestWithoutTheRequestersLanId_When_Ruled_Then_ItStillReachesTheDesk()
    {
        // Given - a second person must still be asked; the body names "An operator"
        var envelope = NotificationEvents.Envelope("PersonalDataErasureRequested", Request(), initiatorLanId: null);

        // When
        var draft = NotificationRules.For("PersonalDataErasureRequested", envelope).Single();

        // Then
        draft.Audience.Should().Be(OperationsDesk);
        draft.Body.Should().StartWith("An operator asks to erase the personal data of CUS-000123");
    }

    [TestCase("CustomerPersonalDataErased", "ERASED", "Erasure approved", "mitur approved your request: the personal data of CUS-000123 is erased.")]
    [TestCase("PersonalDataErasureRejected", "REJECTED", "Erasure rejected", "mitur rejected your request to erase CUS-000123. Nothing was erased; the reason is on the erasure screen.")]
    public void Given_ADecision_When_Ruled_Then_OnlyTheRequesterIsToldWhoDecided(string eventType, string status, string title, string body)
    {
        // Given - the envelope's initiator is the requester, the decider is in the payload
        var envelope = NotificationEvents.Envelope(eventType, Decision(status));

        // When
        var drafts = NotificationRules.For(eventType, envelope);

        // Then - progress for the requester (subject lower-cased), not the desk
        var draft = drafts.Should().ContainSingle().Subject;
        draft.Audience.Should().Be(NotificationAudiences.ForUser(NotificationEvents.RequesterUserId));
        draft.Audience.Should().Be("user:8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a01");
        draft.Category.Should().Be(NotificationCategory.Progress);
        draft.Title.Should().Be(title);
        draft.Body.Should().Be(body);
    }

    [TestCase("CustomerPersonalDataErased", "ERASED")]
    [TestCase("PersonalDataErasureRejected", "REJECTED")]
    public void Given_ADecisionWithoutAKnownRequester_When_Ruled_Then_NobodyIsNotified(string eventType, string status)
    {
        // Given - no initiator: telling the desk instead would be a broadcast
        var envelope = NotificationEvents.Envelope(eventType, Decision(status), initiatorUserId: null);

        // When
        var drafts = NotificationRules.For(eventType, envelope);

        // Then
        drafts.Should().BeEmpty();
    }

    [TestCase("PersonalDataErasureRequested")]
    [TestCase("CustomerPersonalDataErased")]
    [TestCase("PersonalDataErasureRejected")]
    public void Given_AnyErasureEvent_When_Ruled_Then_AClickOpensTheErasureScreenOfTheOperationsApp(string eventType)
    {
        // Given
        var envelope = NotificationEvents.Envelope(eventType, Decision("ERASED"));

        // When
        var draft = NotificationRules.For(eventType, envelope).Single();

        // Then - no record ID: the screen lists the requests, the click carries nothing personal
        var target = NotificationEvents.TargetOf(draft.Target);
        target.GetProperty("mfe").GetString().Should().Be("operations");
        target.GetProperty("path").GetString().Should().Be(ErasurePage);
        target.TryGetProperty("recordId", out _).Should().BeFalse();
    }

    [TestCase("PersonalDataErasureRequested")]
    [TestCase("CustomerPersonalDataErased")]
    [TestCase("PersonalDataErasureRejected")]
    public void Given_AnErasureEventThatAlsoCarriesTheCustomersName_When_Ruled_Then_TheNameReachesNoNotification(string eventType)
    {
        // Given - a producer mistake must not put the erased person into a lasting notification
        var envelope = NotificationEvents.Envelope(eventType, new
        {
            ErasureRequestId = 7, CustomerNumber = "CUS-000123", ReasonCode = "CUSTOMER_REQUEST", DecisionByLanId = "mitur",
            ApplicantName = "Camilla Parker", HolderName = "Camilla Parker",
            Applicant = new { FirstName = "Camilla", LastName = "Parker" }
        });

        // When
        var drafts = NotificationRules.For(eventType, envelope);

        // Then
        drafts.Should().ContainSingle();
        JsonSerializer.Serialize(drafts).Should().NotContainAny("Camilla", "Parker");
    }

    [Test]
    public void Given_AnErasureEventWithoutACustomerNumber_When_Ruled_Then_TheBodySaysACustomer()
    {
        // Given
        var envelope = NotificationEvents.Envelope("PersonalDataErasureRequested", new { ErasureRequestId = 7, ReasonCode = "CUSTOMER_REQUEST" });

        // When
        var draft = NotificationRules.For("PersonalDataErasureRequested", envelope).Single();

        // Then
        draft.Body.Should().Contain("the personal data of a customer");
    }

    [Test]
    public void Given_AContextConfirmingItsCopyIsGone_When_Ruled_Then_NobodyIsNotified()
    {
        // Given - the per-context confirmations are for the audit trail, not for people
        var envelope = NotificationEvents.Envelope("PersonalDataCopyErased", new { ErasureRequestId = 7, CustomerNumber = "CUS-000123", Context = "customer-kyc" });

        // When
        var drafts = NotificationRules.For("PersonalDataCopyErased", envelope);

        // Then
        drafts.Should().BeEmpty();
    }
}