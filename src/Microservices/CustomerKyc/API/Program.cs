using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.AssignKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.DecideVerificationStage;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.OpenKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Queries;
using EnterpriseWebPlatform.CustomerKyc.Api.Authorization;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

// ┌─ :: ARCHITECTURE + SECURITY :: Customer KYC API: a resource server ────────────────────────────
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
var clientIds = ClientIds.From(builder.Configuration).Require(nameof(ClientIds.KycCaseOpeningSubscriber));
builder.Services.AddSingleton(clientIds);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// │ over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("customer-kyc-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("KycDbConnection")
    ?? throw new InvalidOperationException("Connection string 'KycDbConnection' was not configured.");
builder.Services.AddDbContext<KycDbContext>(o => o.UseNpgsql(DependencyOutage.FailFast(connectionString)));

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

    options.AddPolicy("KycCaseOpeningSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.write");
        policy.RequireClaim(
            "client_id",
            clientIds.KycCaseOpeningSubscriber);
    });

    options.AddPolicy("KycCaseView", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.read");
        policy.RequireClaim("role", "kyc_officer");
        policy.RequireClaim("permission", "kyc.case.view");
        policy.RequireClaim("department", "KYC");
    });

    // Stage decisions: the decision permission AND the stage permission.
    // Step-up (MFA): a KYC officer's stage decisions (claiming a case is not a decision) need a sign-in with the authenticator code while "Mfa:Enabled"
    // is true (the token's amr contains "mfa"); with MFA off, nothing changes.
    string[] stepUpPolicies = ["KycIdentityApprove", "KycIdentityReject", "KycDocumentApprove", "KycDocumentReject"];

    void AddDecisionPolicy(string name, params string[] permissions) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", "customer-kyc.write");
            policy.AddRequirements(new KycCaseDecisionRequirement(permissions));
            if (stepUpPolicies.Contains(name))
                policy.RequireMfa();
        });

    AddDecisionPolicy("KycIdentityApprove", "kyc.case.approve", "kyc.identity.verify");
    AddDecisionPolicy("KycIdentityReject", "kyc.case.reject", "kyc.identity.verify");
    AddDecisionPolicy("KycDocumentApprove", "kyc.case.approve", "kyc.document.verify");
    AddDecisionPolicy("KycDocumentReject", "kyc.case.reject", "kyc.document.verify");

    // Claiming / releasing a case changes its assignment (ReBAC relationship).
    AddDecisionPolicy("KycCaseAssign", "kyc.case.update");
});

builder.Services.AddSingleton<IAuthorizationHandler, KycCaseDecisionAuthorizationHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IKycUnitOfWork>(sp => sp.GetRequiredService<KycDbContext>());
builder.Services.AddScoped<IInboxStore>(sp => sp.GetRequiredService<KycDbContext>());
builder.Services.AddScoped<IKycCaseRepository, KycCaseRepository>();
builder.Services.AddScoped<IKycCaseQueries, KycCaseQueries>();
builder.Services.AddScoped<IStaffDirectory, StaffDirectory>();
builder.Services.AddScoped<OpenKycCaseCommandHandler>();
builder.Services.AddScoped<DecideVerificationStageCommandHandler>();
builder.Services.AddScoped<AssignKycCaseCommandHandler>();
// The in-process Outbox relay's Kafka settings, validated at start-up (no credentials, no start).
// :: SECURITY :: Kafka authentication, fail closed - see AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs
builder.Services.AddKafkaOptions(builder.Configuration);
builder.Services.AddSingleton<KycKafkaProducer>();
builder.Services.AddScoped<KycOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(KycOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<KycOutboxPublisherHostedService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.CustomerKyc.Api.Controllers.ApiExceptionHandler>();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Health endpoints for the orchestrator's probes: /health/live (process working)
// │ and /health/ready (dependencies reachable). Anonymous; no internals in the body.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<KycDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    // The in-process Outbox relay: stuck loop -> not live; parked / old rows -> Degraded.
    .Add(new HealthCheckRegistration(
        "outbox-relay",
        sp => new LoopHeartbeatHealthCheck(
            sp.GetRequiredKeyedService<LoopHeartbeat>(KycOutboxPublisherHostedService.HeartbeatKey),
            "KYC Outbox relay",
            TimeSpan.FromMinutes(3)),
        failureStatus: HealthStatus.Unhealthy,
        tags: [HealthEndpoints.LiveTag]))
    .Add(new HealthCheckRegistration(
        "outbox-backlog",
        sp => new OutboxBacklogHealthCheck(
            ct => KycOutboxPublisher.GetBacklogAsync(sp.GetRequiredService<KycDbContext>(), sp.GetRequiredService<IConfiguration>(), ct),
            TimeSpan.FromMinutes(2)),
        failureStatus: HealthStatus.Degraded,
        tags: [HealthEndpoints.ReadyTag]));

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
// :: SECURITY :: Per-caller rate limits - see Common/WebUtilities/Security/RateLimiting.cs
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The internal endpoints (internal/v1, called by the Kafka subscribers) are limited
// │ too, by the machine client's budget - a stolen worker secret cannot flood its API.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1", "/internal");
builder.Services.AddEwpMfaStepUp();   // the step-up requirement and its clear 403 ("mfa_required")
// :: SECURITY :: Step-up MFA - see Common/WebUtilities/Security/MfaStepUp.cs
builder.RequireMfaOutsideDevelopment();   // fail closed: MFA can be off on a developer's machine only

var app = builder.Build();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ One structured log line per request (Serilog), with the caller and the trace ID.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpRequestLogging();

// Problem details without internals; see ApiExceptionHandler.
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Unexpected errors become problem details with a traceId only - no stack trace, SQL or
// │ exception text reaches the caller; the details are logged under the same traceId.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseExceptionHandler();
// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ A dependency unavailable (database, IDP): 503 + Retry-After at once - a page that retries itself, or
// │ Problem Details for a call - instead of a white frame (Common.WebUtilities/Resilience).
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpOutageAnswer();
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

// Remember the acting officer's LAN ID (shown on screens and in events; rules use "sub").
app.Use(async (context, next) =>
{
    await context.RequestServices.GetRequiredService<IStaffDirectory>().RememberAsync(
        context.User.FindFirst("sub")?.Value, context.User.FindFirst("lan_id")?.Value, context.RequestAborted);
    await next();
});

app.UseAuthorization();
// Deny by default: every controller endpoint needs an authenticated caller, on top of its own policy.
app.MapEwpApiControllers();

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program { }