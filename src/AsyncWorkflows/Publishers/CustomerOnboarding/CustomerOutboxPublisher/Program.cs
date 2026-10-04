using EnterpriseWebPlatform.Common.Observability;
using Npgsql;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Configuration;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.HostedServices;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Persistence;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Publishing;

var builder = Host.CreateApplicationBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("customer-outbox-publisher", tracing => tracing.AddNpgsql());

// Validated at start-up: configured for SASL without credentials -> the relay refuses to start.
builder.Services.AddKafkaOptions(builder.Configuration);

builder.Services.AddSingleton<IKafkaProducer, KafkaProducer>();

builder.Services.Configure<CustomerOutboxPublisherOptions>(
    builder.Configuration.GetSection(CustomerOutboxPublisherOptions.SectionName));

builder.Services.AddDbContext<CustomerOutboxDbContext>(options =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("CustomerDbConnection"));
});

builder.Services.AddScoped<ICustomerOutboxPublisher, CustomerOutboxPublisher>();

builder.Services.AddSingleton<LoopHeartbeat>();
builder.Services.AddHostedService<CustomerOutboxPublisherHostedService>();

// Health endpoints for the orchestrator's probes, served on Health:Urls:
//  live  - the relay loop is cycling;
//  ready - the Outbox database is reachable; Degraded when rows are parked or old.
builder.AddWorkerHealthEndpoints();
builder.Services.AddHealthChecks()
    .Add(new HealthCheckRegistration(
        "outbox-relay",
        sp => new LoopHeartbeatHealthCheck(sp.GetRequiredService<LoopHeartbeat>(), "Customer Outbox relay", TimeSpan.FromMinutes(3)),
        failureStatus: HealthStatus.Unhealthy,
        tags: [HealthEndpoints.LiveTag]))
    .AddDbContextCheck<CustomerOutboxDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    .Add(new HealthCheckRegistration(
        "outbox-backlog",
        sp => new OutboxBacklogHealthCheck(
            ct => CustomerOutboxPublisher.GetBacklogAsync(
                sp.GetRequiredService<CustomerOutboxDbContext>(),
                sp.GetRequiredService<IOptions<CustomerOutboxPublisherOptions>>().Value.MaxAttempts,
                ct),
            TimeSpan.FromMinutes(2)),
        failureStatus: HealthStatus.Degraded,
        tags: [HealthEndpoints.ReadyTag]));

var host = builder.Build();

await host.RunAsync();