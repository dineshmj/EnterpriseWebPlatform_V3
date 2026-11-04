using System.Text.Json;

using AwesomeAssertions;
using PactNet;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support;
using EnterpriseWebPlatform.Tests.Contracts;

using static EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support.ContractExamples;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Payments;

// ┌─ :: ARCHITECTURE :: Contract: Accounts' replies to the saga (Kafka) ───────────────────────────
// │ Why : The saga matches each reply to its command by CausationId and acts on the reply type,
// │       the refusal reason and "nothing was held". A reply Accounts reshaped would leave payments
// │       waiting until their timeout - or compensate on the wrong reason.
// │ How : Per reply type, the envelope fields this courier READS; the courier must process such a
// │       message. Written to payments-saga-reply-subscriber-accounts-api.json and checked against
// │       the Accounts API's real Outbox replies (Accounts.Api.Tests/Contracts).
// │ Ref : Integration-Event-Catalogue 4.5 | tst/contracts/README.md | Pact message pacts
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Contract")]
public sealed class PaymentsSagaReplyProcessor_ProcessAsync_Contract_RepliesFromAccounts
{
    private IMessagePactBuilderV4 _pact = null!;

    [SetUp]
    public void SetUp() =>
        _pact = Pact.V4(ContractFiles.PaymentsSagaReplySubscriber, ContractFiles.AccountsApi, Config).WithMessageInteractions();

    [TestCase("FundsReserved", "HELD")]
    [TestCase("FundsSettled", "SETTLED")]
    public async Task Given_AReplyWithoutAReason_When_TheCourierReadsIt_Then_ItHandsItToTheSaga(string reply, string holdStatus)
    {
        await VerifyAsync(reply, new
        {
            MessageId = Guid(ReplyMessageId),
            EventType = reply,
            CausationId = Guid(CommandMessageId),
            Payload = new { PaymentRef = Guid(PaymentRef), PaymentNumber = Text(PaymentNumber), HoldStatus = Text(holdStatus) }
        });
    }

    [Test]
    public async Task Given_AReservationRefused_When_TheCourierReadsIt_Then_TheReasonReachesTheSaga()
    {
        var api = await VerifyAsync("FundsReservationFailed", new
        {
            MessageId = Guid(ReplyMessageId),
            EventType = "FundsReservationFailed",
            CausationId = Guid(CommandMessageId),
            Payload = new
            {
                PaymentRef = Guid(PaymentRef),
                PaymentNumber = Text(PaymentNumber),
                HoldStatus = Text("REFUSED"),
                Reason = Text("INSUFFICIENT_FUNDS"),
                ReasonText = Text("The account does not have enough available funds.")
            }
        });

        var handedOver = JsonDocument.Parse(api.Bodies.Single()).RootElement;
        handedOver.GetProperty("reasonCode").GetString().Should().Be("INSUFFICIENT_FUNDS");
    }

    [Test]
    public async Task Given_AReleaseOfNothing_When_TheCourierReadsIt_Then_NothingWasHeldReachesTheSaga()
    {
        var api = await VerifyAsync("FundsReleased", new
        {
            MessageId = Guid(ReplyMessageId),
            EventType = "FundsReleased",
            CausationId = Guid(CommandMessageId),
            Payload = new
            {
                PaymentRef = Guid(PaymentRef),
                PaymentNumber = Text(PaymentNumber),
                HoldStatus = Text("RELEASED"),
                NothingWasHeld = PactNet.Matchers.Match.Type(true)
            }
        });

        var handedOver = JsonDocument.Parse(api.Bodies.Single()).RootElement;
        handedOver.GetProperty("nothingWasHeld").GetBoolean().Should().BeTrue();
    }

    private async Task<RecordingApi> VerifyAsync(string reply, object envelope)
    {
        // Given
        var api = new RecordingApi();
        var courier = Couriers.PaymentsSagaReply(RecordingApi.Address, api);

        await _pact
            .ExpectsToReceive($"a {reply} reply")
            .WithJsonContent(envelope, EnvelopeJson)
            .VerifyAsync<JsonElement>(async message =>
            {
                // When
                var outcome = await courier.ProcessAsync(Couriers.Consumed(KafkaTopicNames.AccountsFundsReplies, message.GetRawText()), CancellationToken.None);

                // Then
                outcome.Should().Be(ProcessingOutcome.Processed);
            });

        JsonDocument.Parse(api.Bodies.Should().ContainSingle().Subject).RootElement
            .GetProperty("causationId").GetString().Should().Be(CommandMessageId, "the saga accepts only the answer to its current command");
        return api;
    }
}