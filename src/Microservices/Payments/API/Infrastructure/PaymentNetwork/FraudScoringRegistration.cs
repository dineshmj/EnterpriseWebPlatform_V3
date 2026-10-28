using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Polly;

using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Application.Fraud;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;

public static class FraudScoringRegistration
{
    /// <summary>The resilience pipeline of the fraud scoring (its own: a bulkhead next to "payments" and "lookups").</summary>
    public const string Pipeline = "fraud-scoring";

    /// <summary>
    /// The HTTP client to the network's fraud scoring (<see cref="IFraudScoring"/>) with its
    /// pipeline. Returns the client builder (a test gives it its own primary handler).
    /// </summary>
    public static IHttpClientBuilder AddFraudScoringClient(this IServiceCollection services)
    {
        var client = services
            .AddHttpClient<IFraudScoring, FraudScoringClient>(FraudScoringClient.HttpClientName, (sp, client) =>
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PaymentNetworkOptions>>().Value.BaseUrl))
            .WithoutRedirects();   // a 3xx never moves the call, and its API key and the customer's payment, elsewhere

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ The fraud scoring's pipeline - built for a latency budget, not for persistence:
        // │  - timeout         : the budget (Payments:FraudCheck:BudgetMilliseconds) for the one call
        // │  - no retry        : a second try would not fit in the budget; the fallback decides
        // │  - circuit breaker : opens when half of at least 5 calls in 30 s fail, then skips the
        // │                      provider for 15 s - during an outage every payment goes straight
        // │                      to the fallback instead of waiting out the budget first
        // │ Its own instance: a scoring outage never opens the payments' or the lookups' breaker.
        // └──────────────────────────────────────────────────────────────────────────────────────
        client.AddResilienceHandler(Pipeline, (pipeline, context) =>
        {
            var budget = context.ServiceProvider.GetRequiredService<FraudCheckPolicy>().Budget;
            pipeline
                .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    MinimumThroughput = 5,
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = TimeSpan.FromSeconds(15)
                })
                .AddTimeout(budget);
        });
        return client;
    }
}