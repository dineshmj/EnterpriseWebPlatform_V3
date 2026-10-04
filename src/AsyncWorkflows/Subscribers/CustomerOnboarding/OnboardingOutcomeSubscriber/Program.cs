using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.OnboardingOutcomeSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.OnboardingOutcomeSubscriber.Processing;
using EnterpriseWebPlatform.Common.Observability;

var builder = Host.CreateApplicationBuilder(args);

// Distributed tracing: each message is processed in a span that continues the
// producer's trace (Kafka "traceparent" header) and carries it to the API call.
builder.AddEwpObservability("onboarding-outcome-subscriber");

// The shared reliable consume loop (commit after processing, retry in place,
// dead-letter) with this worker's processor.
builder.Services.AddKafkaSubscriber<OnboardingOutcomeProcessor, OnboardingOutcomeSubscriberOptions>(
    builder.Configuration, OnboardingOutcomeSubscriberOptions.SectionName);

// The worker's own machine identity; the token is cached until shortly before expiry.
builder.Services
    .AddCachedM2MTokenClient<OnboardingOutcomeSubscriberOptions>(OnboardingOutcomeSubscriberOptions.SectionName)
    .AddStandardResilienceHandler();

builder.Services
    .AddHttpClient(OnboardingOutcomeProcessor.HttpClientName, (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<OnboardingOutcomeSubscriberOptions>>().Value;
        client.BaseAddress = new Uri(options.CustomerOnboardingApiBaseUrl.TrimEnd('/'));
    })
    // Resilience pipeline around every call to the Customer Onboarding API:
    //  - attempt timeout     : a hung call is abandoned after 10 s
    //  - retry               : 3 retries, exponential back-off with jitter, on
    //                          network errors, timeouts, 408, 429 and 5xx. POST is
    //                          retried deliberately: the endpoint is idempotent per
    //                          MessageId (Inbox), so a retry cannot apply twice.
    //  - circuit breaker     : opens when half of at least 5 calls in 60 s fail, and
    //                          rejects calls for 30 s, giving the API room to recover
    //                          instead of being hammered by every retry
    //  - total timeout       : 60 s for the whole sequence
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.UseJitter = true;
        options.CircuitBreaker.MinimumThroughput = 5;
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
    });

// Health endpoints for the orchestrator's probes, served on Health:Urls:
// live = the consume loop runs; ready = joined the consumer group (Degraded while retrying).
builder.AddWorkerHealthEndpoints();

var host = builder.Build();
await host.RunAsync();