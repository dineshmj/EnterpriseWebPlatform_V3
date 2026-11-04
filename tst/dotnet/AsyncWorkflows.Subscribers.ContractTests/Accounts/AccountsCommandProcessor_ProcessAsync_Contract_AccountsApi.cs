using System.Net;
using System.Text.Json;

using AwesomeAssertions;
using PactNet;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support;
using EnterpriseWebPlatform.Tests.Contracts;

using static EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support.ContractExamples;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Accounts;

// ┌─ :: ARCHITECTURE + RESILIENCE :: Contract: the courier's call to the Accounts API ─────────────
// │ Why : The courier forwards each funds command to POST /internal/v1/accounts/funds/commands and
// │       decides from the status alone: 2xx processed, 400 / 409 dead-letter, everything else
// │       retried. If Accounts changed a field name or answered a refusal with 500, payments would
// │       be retried for ever or parked by mistake.
// │ How : Pact's mock server plays Accounts; the REAL courier sends what it sends. Each interaction
// │       names the state Accounts must be in (provider state) and the status the courier relies
// │       on; the contract accounts-command-subscriber-accounts-api.json is then replayed against
// │       the real Accounts API (Accounts.Api.Tests/Contracts).
// │ Ref : Integration-Event-Catalogue 4.5, 4.6 | tst/contracts/README.md
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Contract")]
public sealed class AccountsCommandProcessor_ProcessAsync_Contract_AccountsApi
{
    private const string Path = "/internal/v1/accounts/funds/commands";

    private IPactBuilderV4 _pact = null!;

    [SetUp]
    public void SetUp() =>
        _pact = Pact.V4(ContractFiles.AccountsCommandSubscriber, ContractFiles.AccountsApi, Config).WithHttpInteractions();

    [TestCase("ReserveFunds", "an open account with enough money in the initiator's branch")]
    [TestCase("SettleFunds", "funds are held for the payment")]
    [TestCase("ReleaseFunds", "funds are held for the payment")]
    public async Task Given_AFundsCommandAccountsCanApply_When_TheCourierForwardsIt_Then_AccountsAnswersOkAndTheMessageIsProcessed(
        string command, string state)
    {
        // Given
        _pact.UponReceiving($"a {command} command")
            .Given(state)
            .WithRequest(HttpMethod.Post, Path)
            .WithJsonBody(Command(command), ApiJson)
            .WillRespond()
            .WithStatus(HttpStatusCode.OK);

        await _pact.VerifyAsync(async context =>
        {
            // When
            var outcome = await Couriers.AccountsCommand(context.MockServerUri)
                .ProcessAsync(Couriers.Consumed(KafkaTopicNames.AccountsCommands, Envelope(command)), CancellationToken.None);

            // Then
            outcome.DeadLetter.Should().BeFalse();
        });
    }

    [Test]
    public async Task Given_NoFundsHeldForThePayment_When_TheCourierForwardsASettleFunds_Then_AccountsAnswersConflictAndTheMessageIsDeadLettered()
    {
        // Given
        _pact.UponReceiving("a SettleFunds command for a payment Accounts holds nothing for")
            .Given("no funds are held for the payment")
            .WithRequest(HttpMethod.Post, Path)
            .WithJsonBody(Command("SettleFunds"), ApiJson)
            .WillRespond()
            .WithStatus(HttpStatusCode.Conflict);

        await _pact.VerifyAsync(async context =>
        {
            // When
            var outcome = await Couriers.AccountsCommand(context.MockServerUri)
                .ProcessAsync(Couriers.Consumed(KafkaTopicNames.AccountsCommands, Envelope("SettleFunds")), CancellationToken.None);

            // Then
            outcome.DeadLetter.Should().BeTrue("a command that contradicts the hold can never succeed - retrying would block the partition");
        });
    }

    /// <summary>The body the courier sends: every field Accounts reads, with what may vary.</summary>
    private static object Command(string command) => new
    {
        commandType = command,
        messageId = Guid(CommandMessageId),
        paymentRef = Guid(PaymentRef),
        paymentNumber = Text(PaymentNumber),
        customerNumber = Text(Customer),
        bsb = Text(Bsb),
        accountNumber = Text(AccountNumber),
        amount = Number(Amount),
        currency = Text("AUD"),
        branchCode = Text(Branch),
        initiatedByUserId = Text(Sophie),
        workflowId = Guid(WorkflowId),
        correlationId = Guid(WorkflowId)
    };

    /// <summary>The Kafka message the courier gets (the shape Payments' contract states).</summary>
    private static string Envelope(string command) => JsonSerializer.Serialize(new
    {
        MessageId = CommandMessageId,
        EventType = command,
        WorkflowId,
        CorrelationId = WorkflowId,
        InitiatedByUserId = Sophie,
        Payload = new
        {
            PaymentRef,
            PaymentNumber,
            CustomerNumber = Customer,
            Bsb,
            AccountNumber,
            Amount,
            Currency = "AUD",
            BranchCode = Branch
        }
    });
}