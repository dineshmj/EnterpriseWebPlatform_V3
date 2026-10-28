using System.Diagnostics;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Application.Fraud;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Infrastructure;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The network's fraud scoring, played by WireMock; the REAL client with the API's own
// │ pipeline (FraudScoringRegistration: the budget as its timeout, a circuit breaker, no
// │ retry). Only a 200 with a known decision and a score 0-999 is an answer; everything
// │ else is "unavailable" - never read as ALLOW - and the fallback rules decide.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class FraudScoringClient_ScoreAsync_Edge_UnusableAnswers
{
    private const string ScoresPath = "/v1/fraud-scores";
    private const string ApiKey = "test-network-key";
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    private WireMockNeighbour _network = null!;
    private IFraudScoring _client = null!;

    [SetUp]
    public void SetUp()
    {
        _network = new WireMockNeighbour();
        _client = ClientTo(_network.Url);
    }

    [TearDown]
    public void TearDown() => _network.Dispose();

    [Test]
    [Category("Security")]
    public async Task Given_TheScoringAnswers_When_APaymentIsScored_Then_ItCarriesTheApiKeyAndThePaymentButNothingAboutTheStaffMember()
    {
        // Given
        _network.Answers("POST", ScoresPath, 200, new { score = 680, decision = "REVIEW", reasons = new[] { "NEW_PAYEE", " ", "HIGH_AMOUNT_FOR_NEW_PAYEE" } });

        // When
        var result = await _client.ScoreAsync(Request(), CancellationToken.None);

        // Then
        result.Should().BeEquivalentTo(new FraudScoringResult(680, FraudDecision.Review, ["NEW_PAYEE", "HIGH_AMOUNT_FOR_NEW_PAYEE"]));
        var sent = _network.Received(ScoresPath).Single();
        WireMockNeighbour.Header(sent, "X-Api-Key").Should().Be(ApiKey);
        var body = JsonNode.Parse(sent.RequestMessage!.Body!)!.AsObject();
        body["endToEndId"]!.GetValue<string>().Should().Be("PAY-261028-FRAUD1");
        body["creditor"]!["accountNumber"]!.GetValue<string>().Should().Be("12345678");
        body["amount"]!.GetValue<decimal>().Should().Be(600m);
        body["firstPaymentToPayee"]!.GetValue<bool>().Should().BeTrue();
        body["channel"]!.GetValue<string>().Should().Be("ASSISTED");
        body.Select(p => p.Key).Should().NotContain(k => k.Contains("initiat", StringComparison.OrdinalIgnoreCase) || k.Contains("staff", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    [Category("Performance")]
    public async Task Given_TheScoringAnswersAfterTheBudget_When_APaymentIsScored_Then_ItIsUnavailableWithinTheBudget()
    {
        // Given: the answer comes 3 s later; the budget is 300 ms.
        _network.Answers("POST", ScoresPath, 200, new { score = 120, decision = "ALLOW" }, after: TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();

        // When
        var score = () => _client.ScoreAsync(Request(), CancellationToken.None);

        // Then: given up at the budget (with room for a slow test machine), never waited out.
        await score.Should().ThrowAsync<FraudScoringUnavailableException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1.5));
    }

    [TestCase(500)]
    [TestCase(503)]
    [TestCase(429)]
    [TestCase(400)]
    public async Task Given_TheScoringFails_When_APaymentIsScored_Then_ItIsUnavailableAndAskedOnceOnly(int status)
    {
        // Given
        _network.Answers("POST", ScoresPath, status);

        // When
        var score = () => _client.ScoreAsync(Request(), CancellationToken.None);

        // Then: no retry - a second try would not fit in the budget.
        await score.Should().ThrowAsync<FraudScoringUnavailableException>().WithMessage($"HTTP {status}");
        _network.Received(ScoresPath).Should().HaveCount(1);
    }

    [TestCase("<html>Gateway error</html>", "*score*")]
    [TestCase("", "*score*")]
    [TestCase("""{"decision":"ALLOW"}""", "*score*")]
    [TestCase("""{"score":1200,"decision":"ALLOW"}""", "*score*")]
    [TestCase("""{"score":-1,"decision":"ALLOW"}""", "*score*")]
    [TestCase("""{"score":120,"decision":"MAYBE"}""", "*unknown decision*")]
    [TestCase("""{"score":120}""", "*unknown decision*")]
    [Category("Security")]
    public async Task Given_AnAnswerThatCannotBeUsed_When_APaymentIsScored_Then_ItIsUnavailable_NeverAnAllow(string body, string why)
    {
        // Given
        _network.Answers("POST", ScoresPath, 200, body);

        // When
        var score = () => _client.ScoreAsync(Request(), CancellationToken.None);

        // Then
        await score.Should().ThrowAsync<FraudScoringUnavailableException>().WithMessage(why);
    }

    [Test]
    [Category("Security")]
    public async Task Given_TheScoringRedirects_When_APaymentIsScored_Then_TheRedirectIsNotFollowed()
    {
        // Given: a 307 would carry the API key and the customer's payment to the other address.
        _network.Server
            .Given(WireMock.RequestBuilders.Request.Create().WithPath(ScoresPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(307).WithHeader("Location", $"{_network.Url}/elsewhere"));
        _network.Answers("POST", "/elsewhere", 200, new { score = 1, decision = "ALLOW" });

        // When
        var score = () => _client.ScoreAsync(Request(), CancellationToken.None);

        // Then
        await score.Should().ThrowAsync<FraudScoringUnavailableException>().WithMessage("HTTP 307");
        _network.Received("/elsewhere").Should().BeEmpty();
    }

    [Test]
    public async Task Given_TheScoringKeepsFailing_When_ItsBreakerOpens_Then_LaterPaymentsAreNotSentToIt()
    {
        // Given: 5 failures in a row open the breaker (half of at least 5 calls in 30 s).
        _network.Answers("POST", ScoresPath, 503);
        for (var i = 0; i < 5; i++)
            await FluentActions.Awaiting(() => _client.ScoreAsync(Request(), CancellationToken.None)).Should().ThrowAsync<FraudScoringUnavailableException>();

        // When
        var score = () => _client.ScoreAsync(Request(), CancellationToken.None);

        // Then: straight to the fallback, without spending the budget on a provider known to be down.
        await score.Should().ThrowAsync<FraudScoringUnavailableException>().WithMessage("*circuit breaker is open*");
        _network.Received(ScoresPath).Should().HaveCount(5);
    }

    private static IFraudScoring ClientTo(string url)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new PaymentNetworkOptions { BaseUrl = url, ApiKey = ApiKey }));
        services.AddSingleton(new FraudCheckPolicy(Budget, 500m));
        services.AddFraudScoringClient();
        return services.BuildServiceProvider().GetRequiredService<IFraudScoring>();
    }

    private static FraudScoringRequest Request() => new(
        Guid.NewGuid(), "PAY-261028-FRAUD1", "CUST-100001", "062-000", "72947086", "Harbour Plumbing Pty Ltd",
        "083-004", "12345678", 600m, "AUD", FirstPaymentToPayee: true);
}