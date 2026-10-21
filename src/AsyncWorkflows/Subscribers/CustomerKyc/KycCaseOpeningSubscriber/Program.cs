using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.KycCaseOpeningSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.KycCaseOpeningSubscriber.Processing;
using EnterpriseWebPlatform.Common.Observability;

// ┌─ :: ARCHITECTURE + SECURITY :: KycCaseOpeningSubscriber: a context-owned worker ───────────────
// │ Why : The context that owns the reaction must own its worker - no central integration hub - and
// │       a worker must not lose, duplicate or wrongly drop a message.
// │ How : Consumes OnboardingApplicationSubmitted and calls the Customer KYC API (open the case)
// │       with its OWN machine identity (client credentials, one client per worker); commits the
// │       offset only after processing; transient failures are retried in place, bad messages go to
// │       the dead-letter topic; the API's Inbox makes redelivery harmless.
// │ Ref : Features 1.1.3, 1.3.2, 1.3.3, 1.6.2, 2.1.2 | Idempotent Consumer pattern |
// │       Dead Letter Channel (EIP)
// └────────────────────────────────────────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// Distributed tracing: each message is processed in a span that continues the
// producer's trace (Kafka "traceparent" header) and carries it to the API call.
builder.AddEwpObservability("kyc-case-opening-subscriber");

// The shared reliable consume loop (commit after processing, retry in place,
// dead-letter) with this worker's processor.
builder.Services.AddKafkaSubscriber<KycCaseOpeningProcessor, KycCaseOpeningSubscriberOptions>(
    builder.Configuration, KycCaseOpeningSubscriberOptions.SectionName)
    .Validate(s => ServiceAddress.IsHttps(s.KycApiBaseUrl), $"{KycCaseOpeningSubscriberOptions.SectionName}:KycApiBaseUrl must be an absolute https address.");

// The worker's own machine identity; the token is cached until shortly before expiry.
builder.Services
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Its own client_id and secret, accepted only on its one internal endpoint (pinned);
    // │ the token is cached until shortly before expiry, never shared with another worker.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddCachedM2MTokenClient<KycCaseOpeningSubscriberOptions>(KycCaseOpeningSubscriberOptions.SectionName)
    .AddStandardResilienceHandler();

builder.Services
    .AddHttpClient(KycCaseOpeningProcessor.HttpClientName, (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<KycCaseOpeningSubscriberOptions>>().Value;
        client.BaseAddress = new Uri(options.KycApiBaseUrl.TrimEnd('/'));
    })
    // Resilience pipeline around every call to the Customer KYC API:
    //  - attempt timeout     : a hung call is abandoned after 10 s
    //  - retry               : 3 retries, exponential back-off with jitter, on
    //                          network errors, timeouts, 408, 429 and 5xx. POST is
    //                          retried deliberately: the endpoint is idempotent
    //                          (Inbox + one case per application).
    //  - circuit breaker     : opens when half of at least 5 calls in 60 s fail, and
    //                          rejects calls for 30 s, giving the API room to recover
    //  - total timeout       : 60 s for the whole sequence
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ Retrying this POST is safe ONLY because the endpoint is idempotent (Inbox / one
    // │ record per message); timeouts, jittered retries and a circuit breaker protect the
    // │ API.
    // └──────────────────────────────────────────────────────────────────────────────────────
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