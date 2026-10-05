using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Notifications.NotificationsSubscriber.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Notifications.NotificationsSubscriber.Processing;
using EnterpriseWebPlatform.Common.Observability;

var builder = Host.CreateApplicationBuilder(args);

// Distributed tracing: each message is processed in a span that continues the
// producer's trace (Kafka "traceparent" header) and carries it to the API call.
builder.AddEwpObservability("notifications-subscriber");

// The shared reliable consume loop (commit after processing, retry in place, dead-letter).
builder.Services.AddKafkaSubscriber<NotificationsProcessor, NotificationsSubscriberOptions>(
    builder.Configuration, NotificationsSubscriberOptions.SectionName);

// The worker's own machine identity; the token is cached until shortly before expiry.
builder.Services
    .AddCachedM2MTokenClient<NotificationsSubscriberOptions>(NotificationsSubscriberOptions.SectionName)
    .AddStandardResilienceHandler();

builder.Services
    .AddHttpClient(NotificationsProcessor.HttpClientName, (sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<NotificationsSubscriberOptions>>().Value.NotificationsApiBaseUrl.TrimEnd('/')))
    // Timeout, jittered retry and circuit breaker; the endpoint is idempotent (Inbox and
    // one notification per event and audience), so retrying the POST is safe.
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