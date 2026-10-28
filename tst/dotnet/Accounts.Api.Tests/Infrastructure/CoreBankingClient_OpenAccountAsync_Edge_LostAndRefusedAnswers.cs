using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.CoreBanking;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Infrastructure;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Core banking, played by WireMock; the REAL client with the API's own pipeline
// │ (CoreBankingRegistration). Only 422 is a refusal - final, never retried. Every other
// │ failure is "unavailable" (the application stays OPENING), and every attempt carries
// │ the SAME Idempotency-Key: a retry can never open a second account.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class CoreBankingClient_OpenAccountAsync_Edge_LostAndRefusedAnswers
{
    private const string AccountsPath = "/v1/accounts";
    private const string ApiKey = "test-core-banking-key";

    private WireMockNeighbour _coreBanking = null!;
    private ICoreBankingSystem _client = null!;

    [SetUp]
    public void SetUp()
    {
        _coreBanking = new WireMockNeighbour();
        _client = ClientTo(_coreBanking.Url);
    }

    [TearDown]
    public void TearDown() => _coreBanking.Dispose();

    [Test]
    [Category("Security")]
    public async Task Given_CoreBankingOpensTheAccount_When_ItIsAsked_Then_TheRequestCarriesTheApiKeyTheIdempotencyKeyAndItsFormat()
    {
        // Given
        var request = Opening();
        _coreBanking.Answers("POST", AccountsPath, 201, new { accountNumber = "10004567", bsb = "062-000", reference = "CB-778899" });

        // When
        var opened = await _client.OpenAccountAsync(request, CancellationToken.None);

        // Then
        opened.Should().Be(new OpenAccountResponse("10004567", "062-000", "CB-778899"));
        var sent = _coreBanking.Received(AccountsPath).Single();
        WireMockNeighbour.Header(sent, "X-Api-Key").Should().Be(ApiKey);
        WireMockNeighbour.Header(sent, "Idempotency-Key").Should().Be(request.IdempotencyKey.ToString());
        var body = JsonNode.Parse(sent.RequestMessage!.Body!)!;
        body["customerNumber"]!.GetValue<string>().Should().Be(request.CustomerNumber);
        body["accountName"]!.GetValue<string>().Should().Be(request.AccountName);
        body["branchCode"]!.GetValue<string>().Should().Be(request.BranchCode);
        body["product"]!.GetValue<string>().Should().Be("EVERYDAY_TRANSACTION");
    }

    [Test]
    public async Task Given_CoreBankingFailsOnceThenOpens_When_ItIsAsked_Then_TheRetryCarriesTheSameIdempotencyKey()
    {
        // Given
        var request = Opening();
        _coreBanking.AnswersInTurn("POST", AccountsPath,
            (503, null), (201, new { accountNumber = "10004568", bsb = "062-000", reference = "CB-778900" }));

        // When
        var opened = await _client.OpenAccountAsync(request, CancellationToken.None);

        // Then: two attempts, one account.
        opened.AccountNumber.Should().Be("10004568");
        _coreBanking.Received(AccountsPath).Select(e => WireMockNeighbour.Header(e, "Idempotency-Key"))
            .Should().HaveCount(2).And.OnlyContain(key => key == request.IdempotencyKey.ToString());
    }

    [Test]
    public async Task Given_CoreBankingRefuses_When_ItIsAsked_Then_ItIsRefusedWithTheReasonAndNotRetried()
    {
        // Given
        _coreBanking.Answers("POST", AccountsPath, 422, new { reason = "The customer number is not known to core banking." });

        // When
        var open = () => _client.OpenAccountAsync(Opening(), CancellationToken.None);

        // Then
        await open.Should().ThrowAsync<CoreBankingRefusedException>().WithMessage("The customer number is not known to core banking.");
        _coreBanking.Received(AccountsPath).Should().HaveCount(1);
    }

    [Test]
    public async Task Given_EveryAnswerIsLate_When_ItIsAsked_Then_ItIsUnavailableAndEveryAttemptCarriesTheSameKey()
    {
        // Given
        var request = Opening();
        _coreBanking.Answers("POST", AccountsPath, 201, new { accountNumber = "10004569", bsb = "062-000", reference = "CB-778901" },
            after: WireMockNeighbour.AttemptTimeout * 2);

        // When
        var open = () => _client.OpenAccountAsync(request, CancellationToken.None);

        // Then: the worker tries again later - with the same key, so still one account.
        await open.Should().ThrowAsync<CoreBankingUnavailableException>();
        (await _coreBanking.ReceivedAsync(AccountsPath, count: 3)).Select(e => WireMockNeighbour.Header(e, "Idempotency-Key"))
            .Should().HaveCount(3).And.OnlyContain(key => key == request.IdempotencyKey.ToString());
    }

    [TestCase(201, """{"accountNumber":"10004570","reference":"CB-778902"}""")]
    [TestCase(200, """{"status":"accepted"}""")]
    [TestCase(200, "<html>Service page</html>")]
    public async Task Given_AnAnswerThatDoesNotIdentifyTheAccount_When_ItIsAsked_Then_ItIsUnavailableNotOpened(int status, string body)
    {
        // Given
        _coreBanking.Answers("POST", AccountsPath, status, body);

        // When
        var open = () => _client.OpenAccountAsync(Opening(), CancellationToken.None);

        // Then: no account number, BSB and reference - nothing to record as opened.
        await open.Should().ThrowAsync<CoreBankingUnavailableException>();
    }

    [Test]
    [Category("Security")]
    public async Task Given_CoreBankingRedirects_When_ItIsAsked_Then_TheRedirectIsNotFollowedAndTheApiKeyStaysHere()
    {
        // Given
        using var elsewhere = new WireMockNeighbour();
        elsewhere.Answers("POST", AccountsPath, 201, new { accountNumber = "99999999", bsb = "999-999", reference = "ELSEWHERE" });
        _coreBanking.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(AccountsPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(307).WithHeader("Location", $"{elsewhere.Url}{AccountsPath}"));

        // When
        var open = () => _client.OpenAccountAsync(Opening(), CancellationToken.None);

        // Then
        await open.Should().ThrowAsync<CoreBankingUnavailableException>().WithMessage("*HTTP 307*");
        elsewhere.Received(AccountsPath).Should().BeEmpty();
    }

    [Test]
    public async Task Given_CoreBankingIsUnreachable_When_ItIsAsked_Then_ItIsUnavailable()
    {
        // Given
        _coreBanking.Server.Stop();

        // When
        var open = () => _client.OpenAccountAsync(Opening(), CancellationToken.None);

        // Then
        await open.Should().ThrowAsync<CoreBankingUnavailableException>();
    }

    private static ICoreBankingSystem ClientTo(string baseUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new CoreBankingOptions { BaseUrl = baseUrl, ApiKey = ApiKey }));
        services.AddCoreBankingClient();
        WireMockNeighbour.ShortenWaits(services, CoreBankingClient.HttpClientName);
        return services.BuildServiceProvider().GetRequiredService<ICoreBankingSystem>();
    }

    private static OpenAccountRequest Opening() =>
        new(Guid.NewGuid(), "CUS-261028-WM0001", "Sophie Turner", "062", AccountProduct.EverydayTransaction);
}