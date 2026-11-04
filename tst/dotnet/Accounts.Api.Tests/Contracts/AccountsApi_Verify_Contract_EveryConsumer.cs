using System.Threading.Channels;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using AwesomeAssertions;
using PactNet;
using PactNet.Infrastructure.Outputters;
using PactNet.Verifier;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Tests.Contracts;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Contracts;

// ┌─ :: ARCHITECTURE :: Provider verification: Accounts keeps every consumer's contract ───────────
// │ Why : A change in Accounts - a renamed field in a funds reply, a refusal that became a 500 on
// │       the funds commands endpoint - must fail HERE, in the pull request that makes it, not in
// │       the payment saga after deployment.
// │ How : Every committed contract naming accounts-api (tst/contracts/pacts) is replayed: HTTP
// │       interactions against the real Accounts API on Kestrel (ContractProviderHost), Kafka replies
// │       from the REAL Outbox mapper (AccountsProviderStates.Replies). Consumers today:
// │       accounts-command-subscriber (funds commands endpoint), payments-saga-reply-subscriber
// │       (funds replies).
// │ Ref : tst/contracts/README.md | Integration-Event-Catalogue 4.5 | Pact provider verification
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Contract")]
[NonParallelizable]
public sealed class AccountsApi_Verify_Contract_EveryConsumer
{
    private readonly AccountsProviderStates _states = new();
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
                services.RemoveAll<IAccountRepository>().AddScoped(_ => _states.Accounts.Object);
                services.RemoveAll<IFundsHoldRepository>().AddScoped(_ => _states.Holds.Object);
                services.RemoveAll<IInboxStore>().AddScoped(_ => _states.Inbox.Object);
                services.RemoveAll<IAccountsUnitOfWork>().AddScoped(_ => _states.UnitOfWork.Object);
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
    public void Given_TheCommittedContracts_When_AccountsIsVerifiedAgainstThem_Then_EveryInteractionIsHonoured()
    {
        // Given
        ContractFiles.FilesFor(ContractFiles.AccountsApi).Select(Path.GetFileName).Should().BeEquivalentTo(
        [
            $"{ContractFiles.AccountsCommandSubscriber}-{ContractFiles.AccountsApi}.json",
            $"{ContractFiles.PaymentsSagaReplySubscriber}-{ContractFiles.AccountsApi}.json"
        ], "a consumer whose contract went missing would no longer be protected");
        using var verifier = new PactVerifier(ContractFiles.AccountsApi, new PactVerifierConfig
        {
            LogLevel = PactLogLevel.Warn,
            Outputters = [new ConsoleOutput()]
        });

        // When
        var verify = () => verifier
            .WithHttpEndpoint(_address)
            .WithMessages(scenarios =>
            {
                foreach (var (description, reply) in AccountsProviderStates.Replies)
                    scenarios.Add(description, reply);
            })
            .WithDirectorySource(new DirectoryInfo(ContractFiles.Directory))
            .WithProviderStateUrl(new Uri(_address, ContractProviderHost.ProviderStatesPath))
            .Verify();

        // Then
        verify.Should().NotThrow("each consumer relies on these exact fields and status codes");
    }

    /// <summary>The machine client calling the funds commands endpoint (its pinned client_id from the API's own configuration).</summary>
    private (string ClientId, string Scope) CallerOf(Microsoft.AspNetCore.Http.PathString path) =>
        (_api.Services.GetRequiredService<ClientIds>().AccountsCommandSubscriber, "accounts.write");
}