using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Tests.Neighbours;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Infrastructure;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The status enquiry settles an unknown outcome, so only a clear record counts: SENT
// │ with its reference, REFUSED, or 404 "never received". Anything else is "cannot tell
// │ yet" - never guessed into sent or not sent.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class PaymentNetworkClient_Enquiries_Edge_StatusAndLookups
{
    private WireMockNeighbour _network = null!;
    private IPaymentNetwork _client = null!;

    [SetUp]
    public void SetUp()
    {
        _network = new WireMockNeighbour();
        _client = PaymentNetworkClient_SendAsync_Edge_LostAndRefusedAnswers.ClientTo(_network.Url);
    }

    [TearDown]
    public void TearDown() => _network.Dispose();

    private static string StatusPath(Guid key) => $"/v1/payments/by-idempotency-key/{key}";

    [Test]
    public async Task Given_TheNetworkHasNoRecord_When_TheStatusIsAsked_Then_ItWasNotReceived()
    {
        // Given
        var key = Guid.NewGuid();
        _network.Answers("GET", StatusPath(key), 404);

        // When
        var status = await _client.GetStatusAsync(key, CancellationToken.None);

        // Then: asked by the payment's own Idempotency-Key.
        status.State.Should().Be(NetworkPaymentState.NotReceived);
        _network.Received(StatusPath(key)).Should().HaveCount(1);
    }

    [Test]
    public async Task Given_TheNetworkSentThePayment_When_TheStatusIsAsked_Then_ItIsSentWithTheReference()
    {
        // Given
        var key = Guid.NewGuid();
        _network.Answers("GET", StatusPath(key), 200, new { status = "SENT", networkReference = "NPP-000200" });

        // When
        var status = await _client.GetStatusAsync(key, CancellationToken.None);

        // Then
        status.Should().Be(new NetworkPaymentStatus(NetworkPaymentState.Sent, "NPP-000200", null));
    }

    [Test]
    public async Task Given_TheNetworkRefusedThePayment_When_TheStatusIsAsked_Then_ItIsRefusedWithTheReason()
    {
        // Given
        var key = Guid.NewGuid();
        _network.Answers("GET", StatusPath(key), 200, new { status = "REFUSED", reason = "The creditor account is closed." });

        // When
        var status = await _client.GetStatusAsync(key, CancellationToken.None);

        // Then
        status.Should().Be(new NetworkPaymentStatus(NetworkPaymentState.Refused, null, "The creditor account is closed."));
    }

    [TestCase(200, """{"status":"SENT"}""")]
    [TestCase(200, """{"status":"PROCESSING"}""")]
    [TestCase(200, "not json")]
    [TestCase(503, null)]
    public async Task Given_NoClearRecord_When_TheStatusIsAsked_Then_ItCannotTellYet(int status, string? body)
    {
        // Given
        var key = Guid.NewGuid();
        _network.Answers("GET", StatusPath(key), status, body);

        // When
        var ask = () => _client.GetStatusAsync(key, CancellationToken.None);

        // Then: neither sent nor not sent - the saga asks again later.
        await ask.Should().ThrowAsync<PaymentNetworkUnavailableException>();
    }

    [Test]
    public async Task Given_AnUnknownBsb_When_ItIsLookedUp_Then_ThereIsNoEntry()
    {
        // Given
        _network.Answers("GET", "/v1/bsb/999-999", 404);

        // When
        var entry = await _client.LookupBsbAsync("999-999", CancellationToken.None);

        // Then
        entry.Should().BeNull();
    }

    [TestCase(200, """{"accountNameHeld":"Harbour Plumbing"}""")]
    [TestCase(500, null)]
    public async Task Given_NoUsableAnswer_When_APayeeIsChecked_Then_TheCheckIsUnavailable(int status, string? body)
    {
        // Given
        _network.Answers("POST", "/v1/payee-confirmations", status, body);

        // When
        var check = () => _client.ConfirmPayeeAsync("083-004", "87654321", "Harbour Plumbing Pty Ltd", CancellationToken.None);

        // Then: no answer is never read as a match.
        await check.Should().ThrowAsync<PaymentNetworkUnavailableException>();
    }
}