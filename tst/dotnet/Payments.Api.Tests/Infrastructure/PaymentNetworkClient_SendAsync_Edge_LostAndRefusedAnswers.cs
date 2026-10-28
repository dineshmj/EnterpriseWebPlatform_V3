using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Infrastructure;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The payment network, played by WireMock; the REAL client with the API's own pipeline
// │ (PaymentNetworkRegistration). Only 422 is a refusal - final, never retried. Every
// │ other failure is "unavailable" and is retried, and every attempt carries the SAME
// │ Idempotency-Key: that is what makes retrying a payment safe.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class PaymentNetworkClient_SendAsync_Edge_LostAndRefusedAnswers
{
    private const string PaymentsPath = "/v1/payments";
    private const string ApiKey = "test-network-key";

    private WireMockNeighbour _network = null!;
    private IPaymentNetwork _client = null!;

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
    public async Task Given_TheNetworkAccepts_When_APaymentIsSent_Then_ItCarriesTheApiKeyTheIdempotencyKeyAndTheNetworksFormat()
    {
        // Given
        var payment = Payment();
        _network.Answers("POST", PaymentsPath, 201, new { networkReference = "NPP-000123" });

        // When
        var reference = await _client.SendAsync(payment, CancellationToken.None);

        // Then
        reference.Should().Be("NPP-000123");
        var sent = _network.Received(PaymentsPath).Single();
        WireMockNeighbour.Header(sent, "X-Api-Key").Should().Be(ApiKey);
        WireMockNeighbour.Header(sent, "Idempotency-Key").Should().Be(payment.IdempotencyKey.ToString());
        var body = JsonNode.Parse(sent.RequestMessage!.Body!)!;
        body["endToEndId"]!.GetValue<string>().Should().Be(payment.PaymentNumber);
        body["debtor"]!["bsb"]!.GetValue<string>().Should().Be(payment.FromBsb);
        body["creditor"]!["accountNumber"]!.GetValue<string>().Should().Be(payment.ToAccountNumber);
        body["creditor"]!["name"]!.GetValue<string>().Should().Be(payment.PayeeName);
        body["amount"]!.GetValue<decimal>().Should().Be(payment.Amount);
        body["currency"]!.GetValue<string>().Should().Be("AUD");
        body["remittanceInformation"]!.GetValue<string>().Should().Be(payment.Reference);
    }

    [Test]
    public async Task Given_TheNetworkFailsOnceThenAccepts_When_APaymentIsSent_Then_TheRetryCarriesTheSameIdempotencyKey()
    {
        // Given
        var payment = Payment();
        _network.AnswersInTurn("POST", PaymentsPath, (503, null), (201, new { networkReference = "NPP-000124" }));

        // When
        var reference = await _client.SendAsync(payment, CancellationToken.None);

        // Then: two attempts, one payment - the network sees the same key twice.
        reference.Should().Be("NPP-000124");
        _network.Received(PaymentsPath).Select(e => WireMockNeighbour.Header(e, "Idempotency-Key"))
            .Should().HaveCount(2).And.OnlyContain(key => key == payment.IdempotencyKey.ToString());
    }

    [Test]
    public async Task Given_TheNetworkRefuses_When_APaymentIsSent_Then_ItIsRefusedWithTheReasonAndNotRetried()
    {
        // Given
        _network.Answers("POST", PaymentsPath, 422, new { reason = "The creditor account is closed." });

        // When
        var send = () => _client.SendAsync(Payment(), CancellationToken.None);

        // Then: a "no" is final - asking again would not change it.
        await send.Should().ThrowAsync<PaymentNetworkRefusedException>().WithMessage("The creditor account is closed.");
        _network.Received(PaymentsPath).Should().HaveCount(1);
    }

    [Test]
    public async Task Given_EveryAnswerIsLate_When_APaymentIsSent_Then_ItIsUnavailableAndEveryAttemptCarriesTheSameKey()
    {
        // Given: the network takes the payment, but each answer comes after the attempt timeout.
        var payment = Payment();
        _network.Answers("POST", PaymentsPath, 201, new { networkReference = "NPP-000125" }, after: WireMockNeighbour.AttemptTimeout * 2);

        // When
        var send = () => _client.SendAsync(payment, CancellationToken.None);

        // Then: not a refusal (the saga asks for the status later), and three attempts of ONE payment.
        await send.Should().ThrowAsync<PaymentNetworkUnavailableException>();
        (await _network.ReceivedAsync(PaymentsPath, count: 3)).Select(e => WireMockNeighbour.Header(e, "Idempotency-Key"))
            .Should().HaveCount(3).And.OnlyContain(key => key == payment.IdempotencyKey.ToString());
    }

    [TestCase(201, """{"networkReference":""}""")]
    [TestCase(200, """{"status":"accepted"}""")]
    [TestCase(200, "<html>Service page</html>")]
    public async Task Given_AnAnswerWithoutTheNetworksReference_When_APaymentIsSent_Then_ItIsUnavailableNotSent(int status, string body)
    {
        // Given
        _network.Answers("POST", PaymentsPath, status, body);

        // When
        var send = () => _client.SendAsync(Payment(), CancellationToken.None);

        // Then: a success without a reference proves nothing - the status enquiry finds out.
        await send.Should().ThrowAsync<PaymentNetworkUnavailableException>();
    }

    [Test]
    [Category("Security")]
    public async Task Given_TheNetworkRedirects_When_APaymentIsSent_Then_TheRedirectIsNotFollowedAndTheApiKeyStaysHere()
    {
        // Given: an answer that points the payment, and its API key, at another host.
        using var elsewhere = new WireMockNeighbour();
        elsewhere.Answers("POST", PaymentsPath, 201, new { networkReference = "NPP-ELSEWHERE" });
        _network.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(PaymentsPath).UsingPost())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(307).WithHeader("Location", $"{elsewhere.Url}{PaymentsPath}"));

        // When
        var send = () => _client.SendAsync(Payment(), CancellationToken.None);

        // Then
        await send.Should().ThrowAsync<PaymentNetworkUnavailableException>().WithMessage("*HTTP 307*");
        elsewhere.Received(PaymentsPath).Should().BeEmpty();
    }

    [Test]
    public async Task Given_TheNetworkIsUnreachable_When_APaymentIsSent_Then_ItIsUnavailable()
    {
        // Given: nobody listens any more.
        _network.Server.Stop();

        // When
        var send = () => _client.SendAsync(Payment(), CancellationToken.None);

        // Then
        await send.Should().ThrowAsync<PaymentNetworkUnavailableException>();
    }

    internal static IPaymentNetwork ClientTo(string baseUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new PaymentNetworkOptions { BaseUrl = baseUrl, ApiKey = ApiKey }));
        services.AddPaymentNetworkClient();
        WireMockNeighbour.ShortenWaits(services, PaymentNetworkClient.HttpClientName);
        return services.BuildServiceProvider().GetRequiredService<IPaymentNetwork>();
    }

    private static NetworkPaymentRequest Payment() => new(
        Guid.NewGuid(), "PAY-261028-WM0001", "062-000", "12345678", "Harbour Plumbing Pty Ltd", "083-004", "87654321", 145.00m, "AUD", "Invoice 1042");
}