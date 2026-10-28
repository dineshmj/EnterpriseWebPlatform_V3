using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.CoreBanking;

public static class CoreBankingRegistration
{
    /// <summary>
    /// The HTTP client to core banking (<see cref="ICoreBankingSystem"/>) with its resilience
    /// pipeline. Returns the client builder (a test gives it its own primary handler).
    /// </summary>
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ Here, not inline in Program.cs, so the adapter tests run the SAME pipeline the API
    // │ runs (retries with the same Idempotency-Key, no redirects) against WireMock.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static IHttpClientBuilder AddCoreBankingClient(this IServiceCollection services)
    {
        var client = services
            .AddHttpClient<ICoreBankingSystem, CoreBankingClient>(CoreBankingClient.HttpClientName, (sp, client) =>
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<CoreBankingOptions>>().Value.BaseUrl))
            .WithoutRedirects();   // a 3xx never moves the call, and its API key, elsewhere

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ Resilience pipeline around every call to core banking:
        // │  - attempt timeout : a hung system is abandoned after 5 s
        // │  - retry           : 2 retries with jittered exponential back-off - safe for this
        // │                      POST ONLY because of the Idempotency-Key (same key, same account)
        // │  - circuit breaker : opens when half of at least 3 calls in 30 s fail and then
        // │                      rejects calls for 30 s, so an outage is not hammered
        // │  - total timeout   : 20 s for the whole sequence
        // │ What still fails leaves the application OPENING for a later retry - never "opened".
        // └──────────────────────────────────────────────────────────────────────────────────────
        client.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.Retry.MaxRetryAttempts = 2;
            options.Retry.UseJitter = true;
            options.CircuitBreaker.MinimumThroughput = 3;
            options.CircuitBreaker.FailureRatio = 0.5;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
        });
        return client;
    }
}