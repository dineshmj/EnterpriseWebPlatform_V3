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
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ :: ARCHITECTURE + SECURITY :: Audit API: a resource server ───────────────────────────────────
// │ Why : The domain API owns its context's data and rules; it trusts nothing the caller says about
// │       itself - not the UI, not the BFF.
// │ How : Validates every JWT access token (issuer, audience, signature, lifetime, at+jwt), denies
// │       by default, checks scope + permission + ABAC / ReBAC per action and inside the
// │       aggregates, writes events through its Outbox and records consumed messages in its Inbox.
// │ Ref : Features 1.1.1, 1.2, 2.2 | OAuth 2.0 resource server (RFC 6750, RFC 9068) |
// │       OWASP API1 / API5 | Authorization-Model 4
// └────────────────────────────────────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The client IDs this API pins in its authorization rules, from its own configuration
// │ (section ClientIds; each must equal the IDP's ClientIds entry). Read now: a missing one
// │ stops the start-up - a pin is never empty.
// └──────────────────────────────────────────────────────────────────────────────────────
var clientIds = ClientIds.From(builder.Configuration).Require(nameof(ClientIds.AuditJourneyApi));
builder.Services.AddSingleton(clientIds);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Traces, Serilog logs and Prometheus metrics (OTLP export when configured).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("audit-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

var connectionString = builder.Configuration.GetConnectionString("AuditDbConnection")
    ?? throw new InvalidOperationException("Connection string 'AuditDbConnection' was not configured.");
builder.Services.AddDbContext<AuditDbContext>(o => o.UseNpgsql(DatabaseOutage.FailFast(connectionString)));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuditTrailAppender>();
builder.Services.AddScoped<AuditChainVerifier>();

// ┌─ SEC + DEBT ─────────────────────────────────────────────────────────────────────────
// │ The chain's anchor outside the database: signed checkpoints in write-once storage. Fail closed -
// │ without a signing key and a directory the API does not start. Development values are in
// │ appsettings.Development.json; elsewhere the key comes from a key vault / HSM and the directory
// │ is immutable storage under another administrator (production items 1 and 2).
// │ See class AuditAnchors.
// └──────────────────────────────────────────────────────────────────────────────────────
var anchorSettings = builder.Configuration.GetSection("Audit:Anchors");
builder.Services.AddSingleton(new AuditCheckpointSigner(anchorSettings["KeyId"] ?? string.Empty, anchorSettings["SigningKey"] ?? string.Empty));
builder.Services.AddSingleton(new AuditAnchorStore(anchorSettings["Directory"] ?? string.Empty));
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
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Every request needs a JWT access token from our IDP: issuer, audience, lifetime and
    // │ signature are checked against the IDP's published keys (metadata over HTTPS only).
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Keep claim names as issued (sub, role, permission, branch, amr, auth_time) - the
        // │ policies read them by these names.
        // └──────────────────────────────────────────────────────────────────────────────────────
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
    // Deny by default: an endpoint without its own policy still needs an authenticated caller.
    // :: SECURITY :: Deny by default - see Common/WebUtilities/Security/ApiEndpointAuthorization.cs
    options.DenyAnonymousByDefault();

    options.AddPolicy(AuditPolicies.View, policy => policy.RequireAuthenticatedUser().AddRequirements(new DelegatedAuditorRequirement("audit.view")));
    options.AddPolicy(AuditPolicies.Search, policy => policy.RequireAuthenticatedUser().AddRequirements(new DelegatedAuditorRequirement("audit.search")));
});
builder.Services.AddSingleton<IAuthorizationHandler, DelegatedAuditorAuthorizationHandler>();

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
// :: SECURITY :: Per-caller rate limits - see Common/WebUtilities/Security/RateLimiting.cs
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
// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ live: the process and its consume loop; ready: the database, the consumer group, and the
// │ chain (Degraded when broken). The checks' numbers also appear in /metrics.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<AuditDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<AuditChainHealthCheck>("audit-chain", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ One structured log line per request (Serilog), with the caller and the trace ID.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpRequestLogging();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Unexpected errors become problem details with a traceId only - no stack trace, SQL or
// │ exception text reaches the caller; the details are logged under the same traceId.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseExceptionHandler();
// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ Database unreachable: 503 + Retry-After at once - a page that retries itself, or
// │ Problem Details for a call - instead of a white frame (Common.WebUtilities/Resilience).
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpDatabaseOutageAnswer();
if (!app.Environment.IsDevelopment())
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ HSTS outside Development only: browsers then refuse plain HTTP for this host (in
    // │ Development it would pin the developer certificate's host).
    // └──────────────────────────────────────────────────────────────────────────────────────
    app.UseHsts();
app.UseHttpsRedirection();
app.UseAuthentication();
// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Order matters: authentication BEFORE the rate limiter (the budget is per person, read
// │ from the token), authorization after it.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpRateLimiting();
app.UseAuthorization();

// Deny by default: every endpoint needs an authenticated caller, on top of its own policy.
app.MapEwpApiControllers();
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program { }