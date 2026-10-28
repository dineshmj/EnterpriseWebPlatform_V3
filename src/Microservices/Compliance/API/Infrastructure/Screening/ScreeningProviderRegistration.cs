using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;

namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Screening;

public static class ScreeningProviderRegistration
{
    /// <summary>
    /// The HTTP client to the screening provider (<see cref="IScreeningProvider"/>) with its
    /// resilience pipeline. Returns the client builder (a test gives it its own primary handler).
    /// </summary>
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ Here, not inline in Program.cs, so the adapter tests run the SAME pipeline the API
    // │ runs (timeouts, retries, no redirects) against WireMock.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static IHttpClientBuilder AddScreeningProviderClient(this IServiceCollection services)
    {
        var client = services
            .AddHttpClient<IScreeningProvider, ScreeningProviderClient>(ScreeningProviderClient.HttpClientName, (sp, client) =>
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ScreeningProviderOptions>>().Value.BaseUrl))
            .WithoutRedirects();   // a 3xx never moves the call, and its API key, elsewhere

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ Resilience pipeline around every call to the external provider:
        // │  - attempt timeout : a hung provider is abandoned after 5 s
        // │  - retry           : 2 retries with jittered exponential back-off (screening is a
        // │                      read-only check, so repeating it is safe)
        // │  - circuit breaker : opens when half of at least 3 calls in 30 s fail and then
        // │                      rejects calls for 30 s, so a provider outage is not hammered
        // │  - total timeout   : 20 s for the whole sequence
        // │ What still fails leaves the case in SCREENING for a later retry - never a pass.
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