using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Audit.Api.Application;
using EnterpriseWebPlatform.Audit.Api.Infrastructure;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.Common.Observability;

var builder = WebApplication.CreateBuilder(args);

// Traces, Serilog logs and Prometheus metrics (OTLP export when configured).
builder.AddEwpObservability("audit-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

var connectionString = builder.Configuration.GetConnectionString("AuditDbConnection")
    ?? throw new InvalidOperationException("Connection string 'AuditDbConnection' was not configured.");
builder.Services.AddDbContext<AuditDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuditTrailAppender>();
builder.Services.AddScoped<AuditChainVerifier>();

// ---------------------------------------------------------------- The trail's input
// The Kafka consumer runs inside this API (the shared, reliable consume loop: in-place
// retry of transient failures, dead-letter topic, liveness and readiness checks). It is the
// ONLY way into the trail: the API has no endpoint that writes an entry.
builder.Services.AddKafkaSubscriber<AuditTrailProcessor, AuditTrailSubscriberOptions>(
    builder.Configuration, AuditTrailSubscriberOptions.SectionName);

// ---------------------------------------------------------------- Tamper evidence
builder.Services.AddSingleton<AuditChainStatus>();
builder.Services.AddHostedService<AuditChainMonitor>();

// ---------------------------------------------------------------- Health
// live: the process and its consume loop; ready: the database, the consumer group, and the
// chain (Degraded when broken). The checks' numbers also appear in /metrics.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<AuditDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<AuditChainHealthCheck>("audit-chain", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();

// The read endpoints (search, timeline, verify) follow with token exchange (step 6e-2): only
// the Audit Journey API, acting for an auditor, will be able to call them.
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program { }