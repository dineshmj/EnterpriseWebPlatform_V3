using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using AwesomeAssertions;
using Moq;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Support;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Subscribers;

// ┌─ SEC + RESIL ────────────────────────────────────────────────────────────────────────
// │ The context's API, played by WireMock; Kafka, by a mock producer. An approved erasure
// │ is passed to the context's own API, and "copy erased" is published ONLY after the API
// │ answered 2xx - with an ID that is the same on every redelivery. An API that is down, or
// │ a failed publish, leaves the message for a retry; a refusal parks it. A request or a
// │ rejection is skipped: nothing to erase.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PersonalDataErasureForwarder_ForwardAsync_Edge_ConfirmOnlyAfterErased
{
    private const string ErasurePath = "/internal/v1/kyc/personal-data-erasures";
    private const string Context = "customer-kyc";

    private WireMockNeighbour _api = null!;
    private TestKeys _keys = null!;
    private Mock<IKafkaProducer> _producer = null!;
    private PersonalDataErasureForwarder _forwarder = null!;
    private PersonalDataErasureTarget _target = null!;

    [SetUp]
    public void SetUp()
    {
        _api = new WireMockNeighbour();
        _keys = new TestKeys();
        _producer = new Mock<IKafkaProducer>();

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient { BaseAddress = new Uri(_api.Url) });
        var tokens = new CachedM2MTokenClient(factory.Object, new TestSettings { IdentityServerAuthority = _api.Url }, _keys.Machine, NullLogger<CachedM2MTokenClient>.Instance);

        _forwarder = new PersonalDataErasureForwarder(
            factory.Object, tokens, _producer.Object, TimeProvider.System, NullLogger<PersonalDataErasureForwarder>.Instance);
        _target = new PersonalDataErasureTarget("CustomerKycApi", ErasurePath, Context);
    }

    [TearDown]
    public void TearDown()
    {
        _api.Dispose();
        _keys.Machine.Dispose();
    }

    private static ConsumedMessage Message(string eventType, Guid messageId, string? customerNumber = "CUS-000123") =>
        new(KafkaTopicNames.CustomerPersonalDataErasure, 0, 42, "7",
            JsonSerializer.Serialize(new
            {
                MessageId = messageId,
                EventType = eventType,
                WorkflowId = Guid.NewGuid(),
                CorrelationId = Guid.NewGuid(),
                InitiatedByUserId = "8f1d7a52-0d0e-4c43-9a8e-0b6d2d0e1a01",
                InitiatedByLanId = "dacoo",
                Payload = new { ErasureRequestId = 7, CustomerNumber = customerNumber, Status = "ERASED" }
            }),
            new Dictionary<string, string>());

    [Test]
    [Category("Happy")]
    public async Task Given_AnApprovedErasure_When_TheApiErasedTheCopy_Then_ItIsConfirmedWithAStableId()
    {
        // Given
        var messageId = Guid.NewGuid();
        _api.Answers("POST", ErasurePath, 200, new { customerNumber = "CUS-000123", recordsErased = 2, recordsHeld = 2 });
        string? published = null;
        _producer.Setup(p => p.ProduceAsync(KafkaTopicNames.CustomerPersonalDataErasureConfirmations, "7", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, payload, _) => published = payload)
            .Returns(Task.CompletedTask);

        // When
        var outcome = await _forwarder.ForwardAsync(Message(PersonalDataErasureForwarder.ErasedEventType, messageId), _target, CancellationToken.None);

        // Then - the API was asked to erase this customer, caused by this message
        outcome.Should().Be(ProcessingOutcome.Processed);
        var asked = JsonDocument.Parse(_api.Received(ErasurePath).Should().ContainSingle().Subject.RequestMessage!.Body!).RootElement;
        asked.GetProperty("customerNumber").GetString().Should().Be("CUS-000123");
        asked.GetProperty("causationId").GetGuid().Should().Be(messageId);

        // Then - the confirmation names the context, the requester and the counts; nothing personal
        var confirmation = JsonDocument.Parse(published!).RootElement;
        confirmation.GetProperty("MessageId").GetGuid().Should().Be(PersonalDataErasureForwarder.ConfirmationIdOf(messageId, Context));
        confirmation.GetProperty("EventType").GetString().Should().Be(PersonalDataErasureForwarder.ConfirmedEventType);
        confirmation.GetProperty("Source").GetString().Should().Be(Context);
        confirmation.GetProperty("CausationId").GetGuid().Should().Be(messageId);
        confirmation.GetProperty("InitiatedByLanId").GetString().Should().Be("dacoo");
        var payload = confirmation.GetProperty("Payload");
        payload.GetProperty("CustomerNumber").GetString().Should().Be("CUS-000123");
        payload.GetProperty("ErasureRequestId").GetInt64().Should().Be(7);
        payload.GetProperty("Status").GetString().Should().Be("ERASED");
        payload.GetProperty("RecordsErased").GetInt32().Should().Be(2);
    }

    [Test]
    public void Given_TheSameErasure_When_ConfirmedByTwoContexts_Then_EachHasItsOwnIdAndARepeatKeepsIt()
    {
        // Given
        var messageId = Guid.NewGuid();

        // When / Then - a redelivery is one audit entry; two contexts are two
        PersonalDataErasureForwarder.ConfirmationIdOf(messageId, Context)
            .Should().Be(PersonalDataErasureForwarder.ConfirmationIdOf(messageId, Context))
            .And.NotBe(PersonalDataErasureForwarder.ConfirmationIdOf(messageId, "compliance"));
    }

    [TestCase("PersonalDataErasureRequested")]
    [TestCase("PersonalDataErasureRejected")]
    public async Task Given_AnErasureNotYetDecided_When_Consumed_Then_NothingIsErasedOrConfirmed(string eventType)
    {
        // When
        var outcome = await _forwarder.ForwardAsync(Message(eventType, Guid.NewGuid()), _target, CancellationToken.None);

        // Then
        outcome.Should().Be(ProcessingOutcome.Processed);
        _api.Received(ErasurePath).Should().BeEmpty();
        _producer.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Given_TheApiIsDown_When_TheErasureArrives_Then_ItIsRetriedAndNothingIsConfirmed()
    {
        // Given
        _api.Answers("POST", ErasurePath, 503, new { title = "A case changed while it was being erased; try again." });

        // When
        var forward = () => _forwarder.ForwardAsync(Message(PersonalDataErasureForwarder.ErasedEventType, Guid.NewGuid()), _target, CancellationToken.None);

        // Then
        await forward.Should().ThrowAsync<TransientProcessingException>();
        _producer.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Given_TheApiRefusesTheRequest_When_TheErasureArrives_Then_ItIsParkedAndNothingIsConfirmed()
    {
        // Given
        _api.Answers("POST", ErasurePath, 400, new { title = "Give the customer number." });

        // When
        var outcome = await _forwarder.ForwardAsync(Message(PersonalDataErasureForwarder.ErasedEventType, Guid.NewGuid()), _target, CancellationToken.None);

        // Then
        outcome.DeadLetter.Should().BeTrue();
        _producer.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Given_TheConfirmationCannotBePublished_When_TheCopyWasErased_Then_TheMessageIsNotDone()
    {
        // Given - Kafka refuses the confirmation
        _api.Answers("POST", ErasurePath, 200, new { recordsErased = 1, recordsHeld = 1 });
        _producer.Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Broker unavailable"));

        // When
        var forward = () => _forwarder.ForwardAsync(Message(PersonalDataErasureForwarder.ErasedEventType, Guid.NewGuid()), _target, CancellationToken.None);

        // Then - the loop retries the whole message: the API answers "done" again, then it is confirmed
        await forward.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestCase(null)]
    [TestCase("   ")]
    public async Task Given_AnErasureWithoutACustomerNumber_When_Consumed_Then_ItIsParked(string? customerNumber)
    {
        // When
        var outcome = await _forwarder.ForwardAsync(
            Message(PersonalDataErasureForwarder.ErasedEventType, Guid.NewGuid(), customerNumber), _target, CancellationToken.None);

        // Then
        outcome.DeadLetter.Should().BeTrue();
        _api.Received(ErasurePath).Should().BeEmpty();
    }

    [Test]
    public async Task Given_NotJson_When_Consumed_Then_ItIsParked()
    {
        // When
        var outcome = await _forwarder.ForwardAsync(
            new ConsumedMessage(KafkaTopicNames.CustomerPersonalDataErasure, 0, 1, null, "not json", new Dictionary<string, string>()),
            _target,
            CancellationToken.None);

        // Then
        outcome.DeadLetter.Should().BeTrue();
        outcome.Reason.Should().StartWith("Malformed JSON");
    }
}