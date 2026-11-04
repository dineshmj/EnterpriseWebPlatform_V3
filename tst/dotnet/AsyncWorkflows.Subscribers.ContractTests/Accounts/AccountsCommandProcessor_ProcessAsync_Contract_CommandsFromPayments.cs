using System.Text.Json;

using AwesomeAssertions;
using PactNet;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support;
using EnterpriseWebPlatform.Tests.Contracts;

using static EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support.ContractExamples;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Accounts;

// ┌─ :: ARCHITECTURE :: Contract: the Payments saga's funds commands (Kafka) ──────────────────────
// │ Why : The courier reads Payments' commands from accounts.commands; if Payments renamed or
// │       dropped a field, every payment would dead-letter at Accounts - found only at run time.
// │ How : This test states, per command, the envelope fields the courier READS (no more: Payments
// │       may add fields freely), proves the courier processes such a message, and writes the
// │       contract accounts-command-subscriber-payments-api.json. The Payments API's tests then
// │       check its real Outbox messages against it (Payments.Api.Tests/Contracts).
// │ Ref : Integration-Event-Catalogue 4.5 | tst/contracts/README.md | Pact message pacts
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Contract")]
public sealed class AccountsCommandProcessor_ProcessAsync_Contract_CommandsFromPayments
{
    private IMessagePactBuilderV4 _pact = null!;

    [SetUp]
    public void SetUp() =>
        _pact = Pact.V4(ContractFiles.AccountsCommandSubscriber, ContractFiles.PaymentsApi, Config).WithMessageInteractions();

    [TestCase("ReserveFunds")]
    [TestCase("SettleFunds")]
    [TestCase("ReleaseFunds")]
    public async Task Given_AFundsCommandFromTheSaga_When_TheCourierReadsIt_Then_ItHandsItToAccounts(string command)
    {
        // Given
        var api = new RecordingApi();
        var courier = Couriers.AccountsCommand(RecordingApi.Address, api);

        await _pact
            .ExpectsToReceive($"a {command} command")
            .WithJsonContent(new
            {
                MessageId = Guid(CommandMessageId),
                EventType = command,
                WorkflowId = Guid(WorkflowId),
                CorrelationId = Guid(WorkflowId),
                InitiatedByUserId = Text(Sophie),
                Payload = new
                {
                    PaymentRef = Guid(PaymentRef),
                    PaymentNumber = Text(PaymentNumber),
                    CustomerNumber = Text(Customer),
                    Bsb = Text(Bsb),
                    AccountNumber = Text(AccountNumber),
                    Amount = Number(Amount),
                    Currency = Text("AUD"),
                    BranchCode = Text(Branch)
                }
            }, EnvelopeJson)
            .VerifyAsync<JsonElement>(async message =>
            {
                // When
                var outcome = await courier.ProcessAsync(Couriers.Consumed(KafkaTopicNames.AccountsCommands, message.GetRawText()), CancellationToken.None);

                // Then
                outcome.Should().Be(ProcessingOutcome.Processed);
            });

        var handedOver = JsonDocument.Parse(api.Bodies.Should().ContainSingle().Subject).RootElement;
        handedOver.GetProperty("commandType").GetString().Should().Be(command);
        handedOver.GetProperty("paymentRef").GetString().Should().Be(PaymentRef);
        handedOver.GetProperty("branchCode").GetString().Should().Be(Branch);
    }
}