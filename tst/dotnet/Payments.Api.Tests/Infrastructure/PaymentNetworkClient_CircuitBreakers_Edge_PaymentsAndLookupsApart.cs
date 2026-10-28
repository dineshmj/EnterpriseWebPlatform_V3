using System.Net;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Infrastructure;

/// <summary>
/// The payment network's two services have a circuit breaker each (PaymentNetworkRegistration):
/// sends failing during an outage must not stop the counter checking a payee - the cause of the
/// intermittent payment-network-resilience failure in the full Playwright run of 28-OCT-2026.
/// Runs the real registration (timeouts, retries, breakers) against a network that refuses
/// payments with 503 and answers lookups.
/// </summary>
[TestFixture]
[Category("Resilience")]
public sealed class PaymentNetworkClient_CircuitBreakers_Edge_PaymentsAndLookupsApart
{
    [Test]
    public async Task Given_SendsFailUntilTheirBreakerOpens_When_APayeeIsChecked_Then_TheCheckIsAnswered()
    {
        // Given - one failing send is 3 attempts: enough to open the payments breaker
        var network = new FakeNetwork();
        var client = ClientTo(network);
        await FluentActions.Awaiting(() => client.SendAsync(Payment(), CancellationToken.None))
            .Should().ThrowAsync<PaymentNetworkUnavailableException>();
        await FluentActions.Awaiting(() => client.SendAsync(Payment(), CancellationToken.None))
            .Should().ThrowAsync<PaymentNetworkUnavailableException>().WithMessage("*circuit breaker is open*");

        // When
        var confirmation = await client.ConfirmPayeeAsync("083-004", "12345678", "Harbour Plumbing Pty Ltd", CancellationToken.None);
        var bsb = await client.LookupBsbAsync("083-004", CancellationToken.None);

        // Then
        confirmation.Result.Should().Be("MATCH");
        bsb.Should().NotBeNull();
        network.LookupCalls.Should().Be(2);
    }

    [TestCase("/v1/payments", PaymentNetworkRegistration.PaymentsPipeline)]
    [TestCase("/v1/payments/by-idempotency-key/7d1f", PaymentNetworkRegistration.PaymentsPipeline)]
    [TestCase("/v1/payee-confirmations", PaymentNetworkRegistration.LookupsPipeline)]
    [TestCase("/v1/bsb/083-004", PaymentNetworkRegistration.LookupsPipeline)]
    public void Given_ARequest_When_ItsPipelineIsChosen_Then_SendsAndStatusAreApartFromLookups(string path, string pipeline)
    {
        // Given / When / Then - absolute (as the handler sees it) and relative
        PaymentNetworkRegistration.PipelineFor(new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri("https://network.test"), path))).Should().Be(pipeline);
        PaymentNetworkRegistration.PipelineFor(new HttpRequestMessage(HttpMethod.Get, path)).Should().Be(pipeline);
    }

    private static IPaymentNetwork ClientTo(FakeNetwork network)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new PaymentNetworkOptions { BaseUrl = "https://network.test", ApiKey = "test-key" }));
        services.AddPaymentNetworkClient().ConfigurePrimaryHttpMessageHandler(() => network);
        return services.BuildServiceProvider().GetRequiredService<IPaymentNetwork>();
    }

    private static NetworkPaymentRequest Payment() => new(
        Guid.NewGuid(), "PAY-261028-TEST01", "062-000", "12345678", "Harbour Plumbing Pty Ltd", "083-004", "12345678", 145.00m, "AUD", "Reconcile sent");

    /// <summary>The payment network during an outage of its payment service: payments 503, lookups answered.</summary>
    private sealed class FakeNetwork : HttpMessageHandler
    {
        private int _lookupCalls;

        public int LookupCalls => _lookupCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v1/payments", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            Interlocked.Increment(ref _lookupCalls);
            var body = path.StartsWith("/v1/bsb/", StringComparison.Ordinal)
                ? """{"bsb":"083-004","bank":"NAB","branch":"Melbourne","state":"VIC","npp":true}"""
                : """{"result":"MATCH","accountNameHeld":null}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}