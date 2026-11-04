using System.Threading.Channels;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using AwesomeAssertions;
using PactNet;
using PactNet.Infrastructure.Outputters;
using PactNet.Verifier;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Tests.Contracts;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Contracts;

// ┌─ :: ARCHITECTURE :: Provider verification: Payments keeps every consumer's contract ───────────
// │ Why : A change in Payments - a renamed field in a funds command, a payment event or an answer,
// │       a refusal that became a 500 - must fail HERE, in the pull request that makes it, not in
// │       Accounts, the reply courier or the Partner Products Worker after deployment.
// │ How : Every committed contract naming payments-api (tst/contracts/pacts) is replayed: HTTP
// │       interactions against the real Payments API on Kestrel (ContractProviderHost), Kafka
// │       messages from the REAL Outbox mapper (PaymentsProviderStates.Messages). Consumers today:
// │       accounts-command-subscriber (commands), payments-saga-reply-subscriber (replies endpoint),
// │       partner-products-worker (payment events + the outcome read).
// │ Ref : tst/contracts/README.md | Integration-Event-Catalogue 4.5 | Pact provider verification
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Contract")]
[NonParallelizable]
public sealed class PaymentsApi_Verify_Contract_EveryConsumer
{
    private readonly PaymentsProviderStates _states = new();
    private WebApplicationFactory<Program> _api = null!;
    private Uri _address = null!;

    [OneTimeSetUp]
    public void StartTheApi()
    {
        ContractFiles.DisablePactTelemetry();
        _api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ForContracts(typeof(Program).Assembly, CallerOf, _states.Set);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPaymentRepository>().AddScoped(_ => _states.Payments.Object);
                services.RemoveAll<IPaymentSagaRepository>().AddScoped(_ => _states.Sagas.Object);
                services.RemoveAll<IInboxStore>().AddScoped(_ => _states.Inbox.Object);
                services.RemoveAll<IPaymentsUnitOfWork>().AddScoped(_ => _states.UnitOfWork.Object);
                services.RemoveAll<IStaffDirectory>().AddScoped(_ => _states.Staff.Object);
            });
        });
        _address = _api.StartOnLocalhost();
    }

    [OneTimeTearDown]
    public void StopTheApi()
    {
        try
        {
            _api.Dispose();
        }
        catch (ChannelClosedException)
        {
            // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
            // │ OpenFeature's API is one static object per process: the factory's shutdown reaches
            // │ it twice, and the second ShutdownAsync finds its event channel already closed.
            // │ Test-only: a deployed API process stops once.
            // └──────────────────────────────────────────────────────────────────────────────────────
        }
    }

    [Test]
    public void Given_TheCommittedContracts_When_PaymentsIsVerifiedAgainstThem_Then_EveryInteractionIsHonoured()
    {
        // Given
        ContractFiles.FilesFor(ContractFiles.PaymentsApi).Select(Path.GetFileName).Should().BeEquivalentTo(
        [
            $"{ContractFiles.AccountsCommandSubscriber}-{ContractFiles.PaymentsApi}.json",
            $"{ContractFiles.PartnerProductsWorker}-{ContractFiles.PaymentsApi}.json",
            $"{ContractFiles.PaymentsSagaReplySubscriber}-{ContractFiles.PaymentsApi}.json"
        ], "a consumer whose contract went missing would no longer be protected");
        using var verifier = new PactVerifier(ContractFiles.PaymentsApi, new PactVerifierConfig
        {
            LogLevel = PactLogLevel.Warn,
            Outputters = [new ConsoleOutput()]
        });

        // When
        var verify = () => verifier
            .WithHttpEndpoint(_address)
            .WithMessages(scenarios =>
            {
                foreach (var (description, message) in PaymentsProviderStates.Messages)
                    scenarios.Add(description, message);
            })
            .WithDirectorySource(new DirectoryInfo(ContractFiles.Directory))
            .WithProviderStateUrl(new Uri(_address, ContractProviderHost.ProviderStatesPath))
            .Verify();

        // Then
        verify.Should().NotThrow("each consumer relies on these exact fields and status codes");
    }

    /// <summary>The machine client each consumer calls with (its pinned client_id from the API's own configuration).</summary>
    private (string ClientId, string Scope) CallerOf(Microsoft.AspNetCore.Http.PathString path)
    {
        var clientIds = _api.Services.GetRequiredService<ClientIds>();
        return path.StartsWithSegments("/internal/v1/payment-sagas")
            ? (clientIds.PaymentsSagaReplySubscriber, "payments.write")
            : (clientIds.PartnerProductsWorker, "payments.read");
    }
}