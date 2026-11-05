using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Compliance.ComplianceCaseOpeningSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Compliance.ComplianceCaseOpeningSubscriber.Processing;
using EnterpriseWebPlatform.Common.Observability;

// ┌─ :: ARCHITECTURE + SECURITY :: ComplianceCaseOpeningSubscriber: a context-owned worker ────────
// │ Why : The context that owns the reaction must own its worker - no central integration hub - and
// │       a worker must not lose, duplicate or wrongly drop a message.
// │ How : Consumes KycCaseApproved and calls the Compliance API (open the case) with its OWN
// │       machine identity (client credentials, one client per worker); commits the offset only
// │       after processing; transient failures are retried in place, bad messages go to the
// │       dead-letter topic; the API's Inbox makes redelivery harmless.
// │ Ref : Features 1.1.3, 1.3.2, 1.3.3, 1.6.2, 2.1.2 | Idempotent Consumer pattern |
// │       Dead Letter Channel (EIP)
// └────────────────────────────────────────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: each message is processed in a span that continues the
// │ producer's trace (Kafka "traceparent" header) and carries it on to the API call.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("compliance-case-opening-subscriber");

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ The shared reliable consume loop with this worker's processor: offset committed only
// │ after processing, transient failures retried in place, bad messages to its
// │ dead-letter topic.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddKafkaSubscriber<ComplianceCaseOpeningProcessor, ComplianceCaseOpeningSubscriberOptions>(
    builder.Configuration, ComplianceCaseOpeningSubscriberOptions.SectionName)
    .Validate(s => ServiceAddress.IsHttps(s.ComplianceApiBaseUrl), $"{ComplianceCaseOpeningSubscriberOptions.SectionName}:ComplianceApiBaseUrl must be an absolute https address.");

// The worker's own machine identity; the token is cached until shortly before expiry.
builder.Services
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Its own client_id and keys - no secret: a one-time assertion signed with its client
    // │ key, and a token bound to its DPoP key; accepted only on its one internal endpoint
    // │ (pinned); the token is cached until shortly before expiry, never shared with another
    // │ worker. The token client has its own resilience (time-outs, no automatic retry).
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddCachedM2MTokenClient<ComplianceCaseOpeningSubscriberOptions>(ComplianceCaseOpeningSubscriberOptions.SectionName);

builder.Services
    .AddHttpClient(ComplianceCaseOpeningProcessor.HttpClientName, (sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ComplianceCaseOpeningSubscriberOptions>>().Value.ComplianceApiBaseUrl.TrimEnd('/')))
    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
    // │ Retrying this POST is safe ONLY because the endpoint is idempotent (Inbox + one case
    // │ per application); timeouts, jittered retries and a circuit breaker protect the API.
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

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Added AFTER the resilience handler (same named client), so every attempt - a retry
// │ too - carries the DPoP-bound token with a new proof of its own (RFC 9449): a reused
// │ proof would be refused.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient(ComplianceCaseOpeningProcessor.HttpClientName).AddDPoPMachineToken();

// The erasure of a customer's personal data: erase this context's copy, then confirm (shared code).
builder.Services.AddPersonalDataErasureForwarder();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Health endpoints for the orchestrator's probes (Health:Urls): live = the consume loop
// │ runs; ready = joined the consumer group (Degraded while a message is retried).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddWorkerHealthEndpoints();

var host = builder.Build();
await host.RunAsync();