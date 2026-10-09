using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Audit.Api.Application;
using EnterpriseWebPlatform.Audit.Api.Authorization;
using EnterpriseWebPlatform.Audit.Api.Controllers;
using EnterpriseWebPlatform.Audit.Api.Infrastructure;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

var builder = WebApplication.CreateBuilder(args);

// Traces, Serilog logs and Prometheus metrics (OTLP export when configured).
builder.AddEwpObservability("audit-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

var connectionString = builder.Configuration.GetConnectionString("AuditDbConnection")
    ?? throw new InvalidOperationException("Connection string 'AuditDbConnection' was not configured.");
builder.Services.AddDbContext<AuditDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuditTrailAppender>();
builder.Services.AddScoped<AuditChainVerifier>();
builder.Services.AddScoped<AuditTrailQueries>();
builder.Services.AddControllers();
builder.Services.AddProblemDetails();

// ---------------------------------------------------------------- Who may read the trail
// Only DELEGATED tokens (token exchange): the Audit Journey API acting for an auditor. The
// person's permissions decide; the acting client must be the Journey API (see
// DelegatedAuditorAuthorization). Everything else - a plain machine token, a BFF's or a
// person's own token - is refused.
var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority was not configured.");
var audience = builder.Configuration["Authentication:Audience"]
    ?? throw new InvalidOperationException("Authentication:Audience was not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            // Access tokens only (RFC 9068 "typ": "at+jwt"): an ID token or any other JWT from the IDP never passes.
            ValidTypes = ["at+jwt"],
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuditPolicies.View, policy => policy.RequireAuthenticatedUser().AddRequirements(new DelegatedAuditorRequirement("audit.view")));
    options.AddPolicy(AuditPolicies.Search, policy => policy.RequireAuthenticatedUser().AddRequirements(new DelegatedAuditorRequirement("audit.search")));
});
builder.Services.AddSingleton<IAuthorizationHandler, DelegatedAuditorAuthorizationHandler>();

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1");

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

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseEwpRateLimiting();
app.UseAuthorization();

// Deny by default: every endpoint needs an authenticated caller, on top of its own policy.
app.MapControllers().RequireAuthorization();
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program { }