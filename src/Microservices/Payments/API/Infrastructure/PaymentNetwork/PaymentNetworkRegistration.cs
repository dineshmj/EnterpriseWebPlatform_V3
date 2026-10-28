using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;

public static class PaymentNetworkRegistration
{
    /// <summary>The resilience pipeline for sending payments and asking for their status.</summary>
    public const string PaymentsPipeline = "payments";

    /// <summary>The resilience pipeline for the lookups: BSB directory and Confirmation of Payee.</summary>
    public const string LookupsPipeline = "lookups";

    /// <summary>
    /// The HTTP client to the payment network (<see cref="IPaymentNetwork"/>) with its resilience
    /// pipelines. Returns the client builder (a test gives it its own primary handler).
    /// </summary>
    public static IHttpClientBuilder AddPaymentNetworkClient(this IServiceCollection services)
    {
        var client = services
            .AddHttpClient<IPaymentNetwork, PaymentNetworkClient>(PaymentNetworkClient.HttpClientName, (sp, client) =>
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PaymentNetworkOptions>>().Value.BaseUrl))
            .WithoutRedirects();   // a 3xx never moves the call, and its API key, elsewhere

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ Resilience pipeline around every call to the payment network:
        // │  - attempt timeout : a hung network is abandoned after 5 s
        // │  - retry           : 2 retries with jittered exponential back-off - safe for this
        // │                      POST ONLY because of the Idempotency-Key (same key, same payment)
        // │  - circuit breaker : opens when half of at least 3 calls in 30 s fail, then rejects
        // │                      calls for 30 s, so an outage is not hammered
        // │  - total timeout   : 20 s for the whole sequence
        // │ What still fails goes to the saga, which retries later or compensates.
        // └──────────────────────────────────────────────────────────────────────────────────────
        client
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.UseJitter = true;
                options.CircuitBreaker.MinimumThroughput = 3;
                options.CircuitBreaker.FailureRatio = 0.5;
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
            })
            // ┌─ :: RESILIENCE :: One circuit breaker per service, not per network ───────────────────────────
            // │ Why : Sending payments and checking a payee are different services (NPP clearing vs
            // │       Confirmation of Payee). With one breaker, failing sends during an outage opened
            // │       it for the lookups too: the counter could not even check a payee ("the payment
            // │       network cannot answer"), although that service answered (full Playwright run,
            // │       28-OCT-2026, payment-network-resilience).
            // │ How : The same pipeline settings, two instances: "payments" (/v1/payments...: send,
            // │       status enquiry) and "lookups" (BSB, Confirmation of Payee) - a bulkhead; each
            // │       breaker opens only on its own failures.
            // │ Ref : Features 1.4.3 | Polly / Microsoft.Extensions.Http.Resilience pipeline selection |
            // │       Release It! (bulkheads)
            // └────────────────────────────────────────────────────────────────────────────────────────────────
            .SelectPipelineBy(_ => PipelineFor);
        return client;
    }

    /// <summary>Which pipeline a request to the payment network goes through.</summary>
    public static string PipelineFor(HttpRequestMessage request)
    {
        var path = request.RequestUri is null ? string.Empty
            : request.RequestUri.IsAbsoluteUri ? request.RequestUri.AbsolutePath
            : request.RequestUri.OriginalString;
        return path.StartsWith("/v1/payments", StringComparison.Ordinal) ? PaymentsPipeline : LookupsPipeline;
    }
}