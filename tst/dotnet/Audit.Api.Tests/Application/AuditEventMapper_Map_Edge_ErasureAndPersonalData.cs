using System.Text.Json;

using AwesomeAssertions;

using EnterpriseWebPlatform.Audit.Api.Application;

namespace EnterpriseWebPlatform.Audit.Api.Tests.Application;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The trail must prove an erasure was lawful - who asked, who decided, why - and must
// │ itself hold nothing that would need erasing: the mapper keeps identifiers, people
// │ (subject + LAN ID) and codes, and drops names, addresses and remarks from any event.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class AuditEventMapper_Map_Edge_ErasureAndPersonalData
{
    private const string Topic = "customer.personal-data.erasure";
    private static readonly DateTimeOffset RecordedAt = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private static string Envelope(string eventType, object payload, string initiatorLanId = "dacoo") =>
        JsonSerializer.Serialize(new
        {
            MessageId = Guid.NewGuid(),
            EventType = eventType,
            SchemaVersion = 1,
            Source = "customer-onboarding",
            OccurredAt = RecordedAt.AddSeconds(-2),
            WorkflowId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            InitiatedByUserId = "8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a01",
            InitiatedByLanId = initiatorLanId,
            Payload = payload
        });

    [Test]
    [Category("Happy")]
    public void Given_AnApprovedErasure_When_Recorded_Then_TheEntryNamesTheRequesterTheDeciderAndTheReason()
    {
        // Given
        var message = Envelope("CustomerPersonalDataErased", new
        {
            ErasureRequestId = 7, CustomerId = 42, CustomerNumber = "CUS-000123", ReasonCode = "CUSTOMER_REQUEST",
            Status = "ERASED", DecisionByUserId = "8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a02", DecisionByLanId = "mitur"
        });

        // When
        var entry = AuditEventMapper.Map(Topic, 0, 11, message, RecordedAt)!;

        // Then
        entry.EventType.Should().Be("CustomerPersonalDataErased");
        entry.RecordType.Should().Be("CUSTOMER");
        entry.RecordRef.Should().Be("CUS-000123");
        entry.Status.Should().Be("ERASED");
        entry.ReasonCode.Should().Be("CUSTOMER_REQUEST");
        entry.InitiatedByLanId.Should().Be("dacoo");
        entry.ActorLanId.Should().Be("mitur");
    }

    [Test]
    public void Given_AContextConfirmingItsCopyIsGone_When_Recorded_Then_TheEntryNamesTheContextAndTheRequester()
    {
        // Given - KYC's subscriber confirms after its API erased the copy
        var message = Envelope("PersonalDataCopyErased", new
        {
            ErasureRequestId = 7, CustomerNumber = "CUS-000123", Context = "customer-kyc", Status = "ERASED",
            RecordsErased = 1, RecordsHeld = 1
        }).Replace("\"Source\":\"customer-onboarding\"", "\"Source\":\"customer-kyc\"");

        // When
        var entry = AuditEventMapper.Map("customer.personal-data.erasure.confirmed", 0, 3, message, RecordedAt)!;

        // Then
        entry.EventType.Should().Be("PersonalDataCopyErased");
        entry.Source.Should().Be("customer-kyc");
        entry.RecordType.Should().Be("CUSTOMER");
        entry.RecordRef.Should().Be("CUS-000123");
        entry.Status.Should().Be("ERASED");
        entry.InitiatedByLanId.Should().Be("dacoo");
    }

    [Test]
    public void Given_ARequestWaitingForASecondPerson_When_Recorded_Then_ItHasNoDeciderYet()
    {
        // Given
        var message = Envelope("PersonalDataErasureRequested", new
        {
            ErasureRequestId = 7, CustomerId = 42, CustomerNumber = "CUS-000123", ReasonCode = "RETENTION_ENDED", Status = "PENDING"
        });

        // When
        var entry = AuditEventMapper.Map(Topic, 0, 10, message, RecordedAt)!;

        // Then
        entry.Status.Should().Be("PENDING");
        entry.ReasonCode.Should().Be("RETENTION_ENDED");
        entry.ActorUserId.Should().BeNull();
        entry.InitiatedByLanId.Should().Be("dacoo");
    }

    [Test]
    public void Given_AnEventCarryingTheApplicantsNameAndAddress_When_Recorded_Then_NothingPersonalReachesTheTrail()
    {
        // Given - the submitted application travels with the applicant in clear (bounded to 7 days)
        var message = Envelope("OnboardingApplicationSubmitted", new
        {
            ApplicationNumber = "APP-20261105-000001", CustomerNumber = "CUS-000123", BranchCode = "SYD001",
            Applicant = new
            {
                FirstName = "Camilla", LastName = "Parker",
                ResidentialAddress = new { AddressLine1 = "1 George Street", City = "Sydney", PostalCode = "2000" }
            },
            DecisionRemarks = "Camilla Parker called in person"
        }, initiatorLanId: "somit");

        // When
        var entry = AuditEventMapper.Map("onboarding.application.submitted", 0, 3, message, RecordedAt)!;

        // Then - the identifiers stay, the person does not (only a hash of the whole message)
        entry.RecordRef.Should().Be("APP-20261105-000001");
        entry.CustomerNumber.Should().Be("CUS-000123");
        JsonSerializer.Serialize(entry).Should().NotContainAny("Camilla", "Parker", "George", "called in person");
        entry.PayloadSha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [TestCase("not json at all")]
    [TestCase("[1, 2, 3]")]
    public void Given_AMessageThatIsNotAJsonObject_When_Recorded_Then_ItIsLeftForTheDeadLetterTopic(string message)
    {
        // When
        var entry = AuditEventMapper.Map(Topic, 0, 12, message, RecordedAt);

        // Then
        entry.Should().BeNull();
    }

    [Test]
    public void Given_TheSameMessageWithoutAnId_When_RecordedTwice_Then_ItGetsTheSameIdEachTime()
    {
        // Given - a redelivery must be recognised as one entry
        const string message = """{"EventType":"PersonalDataErasureRequested","Payload":{"CustomerNumber":"CUS-000123"}}""";

        // When
        var first = AuditEventMapper.Map(Topic, 2, 99, message, RecordedAt)!;
        var again = AuditEventMapper.Map(Topic, 2, 99, message, RecordedAt.AddMinutes(5))!;

        // Then
        again.MessageId.Should().Be(first.MessageId);
    }
}