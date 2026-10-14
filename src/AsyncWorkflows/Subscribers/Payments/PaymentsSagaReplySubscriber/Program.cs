using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Payments.PaymentsSagaReplySubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Payments.PaymentsSagaReplySubscriber.Processing;
using EnterpriseWebPlatform.Common.Observability;

// ┌─ :: ARCHITECTURE + SECURITY :: PaymentsSagaReplySubscriber: a context-owned worker ────────────
// │ Why : The context that owns the reaction must own its worker - no central integration hub - and
// │       a worker must not lose, duplicate or wrongly drop a message.
// │ How : Consumes the Accounts replies to the saga's commands and calls the Payments API with its
// │       OWN machine identity (client credentials, one client per worker); commits the offset only
// │       after processing; transient failures are retried in place, bad messages go to the
// │       dead-letter topic; the API's Inbox makes redelivery harmless.
// │ Ref : Features 1.1.3, 1.3.2, 1.3.3, 1.6.2, 2.1.2 | Idempotent Consumer pattern |
// │       Dead Letter Channel (EIP)
// └────────────────────────────────────────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// Distributed tracing: each message is processed in a span that continues the
// producer's trace (Kafka "traceparent" header) and carries it to the API call.
builder.AddEwpObservability("payments-saga-reply-subscriber");

// The shared reliable consume loop (commit after processing, retry in place, dead-letter).
builder.Services.AddKafkaSubscriber<PaymentsSagaReplyProcessor, PaymentsSagaReplySubscriberOptions>(
    builder.Configuration, PaymentsSagaReplySubscriberOptions.SectionName);

// The worker's own machine identity; the token is cached until shortly before expiry.
builder.Services
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Its own client_id and secret, accepted only on its one internal endpoint (pinned);
    // │ the token is cached until shortly before expiry, never shared with another worker.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddCachedM2MTokenClient<PaymentsSagaReplySubscriberOptions>(PaymentsSagaReplySubscriberOptions.SectionName)
    .AddStandardResilienceHandler();

builder.Services
    .AddHttpClient(PaymentsSagaReplyProcessor.HttpClientName, (sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PaymentsSagaReplySubscriberOptions>>().Value.PaymentsApiBaseUrl.TrimEnd('/')))
    // Timeout, jittered retry and circuit breaker; the endpoint is idempotent (Inbox, and
    // the saga ignores a reply it no longer waits for), so retrying the POST is safe.
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

// Health endpoints for the orchestrator's probes, served on Health:Urls.
builder.AddWorkerHealthEndpoints();

var host = builder.Build();
await host.RunAsync();