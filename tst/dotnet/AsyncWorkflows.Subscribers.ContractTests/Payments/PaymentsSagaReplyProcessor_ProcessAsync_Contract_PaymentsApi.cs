using System.Net;
using System.Text.Json;

using AwesomeAssertions;
using PactNet;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support;
using EnterpriseWebPlatform.Tests.Contracts;

using static EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support.ContractExamples;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Payments;

// ┌─ :: ARCHITECTURE + RESILIENCE :: Contract: the courier's call to the Payments API ─────────────
// │ Why : The courier hands each reply to POST /internal/v1/payment-sagas/replies and decides from
// │       the status alone: 2xx processed, 400 dead-letter, 409 / 5xx retried. A reply for an
// │       unknown payment must be refused with 400 - a 500 would retry it for ever.
// │ How : Pact's mock server plays Payments; the REAL courier sends what it sends. Written to
// │       payments-saga-reply-subscriber-payments-api.json and replayed against the real Payments
// │       API (Payments.Api.Tests/Contracts).
// │ Ref : Integration-Event-Catalogue 4.5, 4.6 | tst/contracts/README.md
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Contract")]
public sealed class PaymentsSagaReplyProcessor_ProcessAsync_Contract_PaymentsApi
{
    private const string Path = "/internal/v1/payment-sagas/replies";

    private IPactBuilderV4 _pact = null!;

    [SetUp]
    public void SetUp() =>
        _pact = Pact.V4(ContractFiles.PaymentsSagaReplySubscriber, ContractFiles.PaymentsApi, Config).WithHttpInteractions();

    [Test]
    public async Task Given_ASagaWaitingForAccounts_When_TheCourierHandsOverFundsReserved_Then_PaymentsAnswersOk()
    {
        // Given
        _pact.UponReceiving("a FundsReserved reply")
            .Given("a payment saga waits for Accounts' reply")
            .WithRequest(HttpMethod.Post, Path)
            .WithJsonBody(Reply("FundsReserved", reasonCode: null, reason: null, nothingWasHeld: null), ApiJson)
            .WillRespond()
            .WithStatus(HttpStatusCode.OK);

        // When / Then
        await VerifyAsync(Envelope("FundsReserved", reason: null, reasonText: null, nothingWasHeld: null), deadLetter: false);
    }

    [Test]
    public async Task Given_ASagaWaitingForAccounts_When_TheCourierHandsOverARefusal_Then_PaymentsAnswersOk()
    {
        // Given
        _pact.UponReceiving("a FundsReservationFailed reply")
            .Given("a payment saga waits for Accounts' reply")
            .WithRequest(HttpMethod.Post, Path)
            .WithJsonBody(Reply("FundsReservationFailed", Text("INSUFFICIENT_FUNDS"), Text("The account does not have enough available funds."), nothingWasHeld: null), ApiJson)
            .WillRespond()
            .WithStatus(HttpStatusCode.OK);

        // When / Then
        await VerifyAsync(Envelope("FundsReservationFailed", "INSUFFICIENT_FUNDS", "The account does not have enough available funds.", nothingWasHeld: null),
            deadLetter: false);
    }

    [Test]
    public async Task Given_ASagaCompensating_When_TheCourierHandsOverFundsReleased_Then_PaymentsAnswersOk()
    {
        // Given
        _pact.UponReceiving("a FundsReleased reply")
            .Given("a payment saga waits for Accounts' reply")
            .WithRequest(HttpMethod.Post, Path)
            .WithJsonBody(Reply("FundsReleased", reasonCode: null, reason: null, PactNet.Matchers.Match.Type(true)), ApiJson)
            .WillRespond()
            .WithStatus(HttpStatusCode.OK);

        // When / Then
        await VerifyAsync(Envelope("FundsReleased", reason: null, reasonText: null, nothingWasHeld: true), deadLetter: false);
    }

    [Test]
    public async Task Given_NoSagaForThePayment_When_TheCourierHandsOverAReply_Then_PaymentsAnswersBadRequestAndTheMessageIsDeadLettered()
    {
        // Given
        _pact.UponReceiving("a reply for a payment Payments does not know")
            .Given("no payment saga exists for the payment")
            .WithRequest(HttpMethod.Post, Path)
            .WithJsonBody(Reply("FundsReserved", reasonCode: null, reason: null, nothingWasHeld: null), ApiJson)
            .WillRespond()
            .WithStatus(HttpStatusCode.BadRequest);

        // When / Then
        await VerifyAsync(Envelope("FundsReserved", reason: null, reasonText: null, nothingWasHeld: null), deadLetter: true);
    }

    private Task VerifyAsync(string envelope, bool deadLetter) =>
        _pact.VerifyAsync(async context =>
        {
            // When
            var outcome = await Couriers.PaymentsSagaReply(context.MockServerUri)
                .ProcessAsync(Couriers.Consumed(KafkaTopicNames.AccountsFundsReplies, envelope), CancellationToken.None);

            // Then
            outcome.DeadLetter.Should().Be(deadLetter);
        });

    /// <summary>The body the courier sends: every field the saga reads, with what may vary (null: the reply has none).</summary>
    private static object Reply(string replyType, object? reasonCode, object? reason, object? nothingWasHeld) => new
    {
        messageId = Guid(ReplyMessageId),
        causationId = Guid(CommandMessageId),
        replyType,
        paymentRef = Guid(PaymentRef),
        reasonCode,
        reason,
        nothingWasHeld
    };

    /// <summary>The Kafka message the courier gets (the shape Accounts' contract states).</summary>
    private static string Envelope(string replyType, string? reason, string? reasonText, bool? nothingWasHeld) => JsonSerializer.Serialize(new
    {
        MessageId = ReplyMessageId,
        EventType = replyType,
        CausationId = CommandMessageId,
        Payload = new { PaymentRef, PaymentNumber, HoldStatus = "HELD", Reason = reason, ReasonText = reasonText, NothingWasHeld = nothingWasHeld }
    });
}