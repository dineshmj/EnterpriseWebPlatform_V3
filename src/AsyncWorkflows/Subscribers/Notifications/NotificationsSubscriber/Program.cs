using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Notifications.NotificationsSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Notifications.NotificationsSubscriber.Processing;
using EnterpriseWebPlatform.Common.Observability;

// ┌─ :: ARCHITECTURE + SECURITY :: NotificationsSubscriber: a context-owned worker ────────────────
// │ Why : The context that owns the reaction must own its worker - no central integration hub - and
// │       a worker must not lose, duplicate or wrongly drop a message.
// │ How : Consumes the workflow events and calls the Notifications API with its OWN machine
// │       identity (client credentials, one client per worker); commits the offset only after
// │       processing; transient failures are retried in place, bad messages go to the dead-letter
// │       topic; the API's Inbox makes redelivery harmless.
// │ Ref : Features 1.1.3, 1.3.2, 1.3.3, 1.6.2, 2.1.2 | Idempotent Consumer pattern |
// │       Dead Letter Channel (EIP)
// └────────────────────────────────────────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: each message is processed in a span that continues the
// │ producer's trace (Kafka "traceparent" header) and carries it on to the API call.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("notifications-subscriber");

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ The shared reliable consume loop with this worker's processor: offset committed only
// │ after processing, transient failures retried in place, bad messages to its
// │ dead-letter topic.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddKafkaSubscriber<NotificationsProcessor, NotificationsSubscriberOptions>(
    builder.Configuration, NotificationsSubscriberOptions.SectionName)
    .Validate(s => ServiceAddress.IsHttps(s.NotificationsApiBaseUrl), $"{NotificationsSubscriberOptions.SectionName}:NotificationsApiBaseUrl must be an absolute https address.");

// The worker's own machine identity; the token is cached until shortly before expiry.
builder.Services
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Its own client_id and secret, accepted only on its one internal endpoint (pinned);
    // │ the token is cached until shortly before expiry, never shared with another worker.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddCachedM2MTokenClient<NotificationsSubscriberOptions>(NotificationsSubscriberOptions.SectionName)
    .AddStandardResilienceHandler();

builder.Services
    .AddHttpClient(NotificationsProcessor.HttpClientName, (sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<NotificationsSubscriberOptions>>().Value.NotificationsApiBaseUrl.TrimEnd('/')))
    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
    // │ Retrying this POST is safe ONLY because the endpoint is idempotent (Inbox and one
    // │ notification per event and audience); timeouts, jittered retries and a circuit
    // │ breaker protect the API.
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

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Health endpoints for the orchestrator's probes (Health:Urls): live = the consume loop
// │ runs; ready = joined the consumer group (Degraded while a message is retried).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddWorkerHealthEndpoints();

var host = builder.Build();
await host.RunAsync();