using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Application.Commands;
using EnterpriseWebPlatform.Compliance.Api.Application.Queries;
using EnterpriseWebPlatform.Compliance.Api.Authorization;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Messaging;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Screening;

// ┌─ :: ARCHITECTURE + SECURITY :: Compliance API: a resource server ──────────────────────────────
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
var clientIds = ClientIds.From(builder.Configuration).Require(nameof(ClientIds.ComplianceCaseOpeningSubscriber), nameof(ClientIds.OperationsJourneyApi));
builder.Services.AddSingleton(clientIds);

// Distributed tracing: W3C trace context across HTTP and Kafka (OTLP export when configured).
builder.AddEwpObservability("compliance-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("ComplianceDbConnection")
    ?? throw new InvalidOperationException("Connection string 'ComplianceDbConnection' was not configured.");
builder.Services.AddDbContext<ComplianceDbContext>(o => o.UseNpgsql(connectionString));

// ---------------------------------------------------------------- Authentication
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

// ---------------------------------------------------------------- Authorization
builder.Services.AddAuthorization(options =>
{
    // Deny by default: an endpoint without its own policy still needs an authenticated caller.
    // :: SECURITY :: Deny by default - see Common/WebUtilities/Security/ApiEndpointAuthorization.cs
    options.DenyAnonymousByDefault();

    // The subscriber's pinned machine identity: nobody else may open cases.
    options.AddPolicy("ComplianceCaseOpeningSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "compliance.write");
        policy.RequireClaim("client_id",
            clientIds.ComplianceCaseOpeningSubscriber);
    });

    // Officer policies: scope for the kind of operation + role, department, clearance
    // and the operation's permission. Case-level rules live in the aggregate.
    // Step-up (MFA): a compliance officer's decision (approve or reject) need a sign-in with the authenticator code while "Mfa:Enabled"
    // is true (the token's amr contains "mfa"); with MFA off, nothing changes.
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Parked Outbox messages: only through the Operations Journey API, acting for an operator
    // │ (delegated token, act = that API) with outbox.view - and, to resend, workflow.reprocess
    // │ and a recent MFA sign-in. Never a screen, a machine or any other client directly.
    // └──────────────────────────────────────────────────────────────────────────────────────
    options.AddPolicy("ComplianceOutboxView", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "compliance.read");
        policy.RequireClaim("permission", "outbox.view");
        policy.RequireDelegationBy(clientIds.OperationsJourneyApi);
    });
    options.AddPolicy("ComplianceOutboxResend", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "compliance.write");
        policy.RequireClaim("permission", "workflow.reprocess");
        policy.RequireDelegationBy(clientIds.OperationsJourneyApi);
        policy.RequireMfa();
    });

    string[] stepUpPolicies = ["ComplianceCaseApprove", "ComplianceCaseReject"];

    void AddOfficerPolicy(string name, string scope, params string[] permissions) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", scope);
            policy.AddRequirements(new ComplianceOfficerRequirement(permissions));
            if (stepUpPolicies.Contains(name))
                policy.RequireMfa();
        });

    AddOfficerPolicy("ComplianceCaseView", "compliance.read", "compliance.case.view");
    AddOfficerPolicy("ComplianceCaseReview", "compliance.write", "compliance.case.review");
    AddOfficerPolicy("ComplianceCaseApprove", "compliance.write", "compliance.case.approve");
    AddOfficerPolicy("ComplianceCaseReject", "compliance.write", "compliance.case.reject");
    AddOfficerPolicy("ComplianceCaseHold", "compliance.write", "compliance.case.hold");
    AddOfficerPolicy("ComplianceCaseReleaseHold", "compliance.write", "compliance.case.release");
});
builder.Services.AddSingleton<IAuthorizationHandler, ComplianceOfficerAuthorizationHandler>();

// ---------------------------------------------------------------- Application
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IComplianceUnitOfWork>(sp => sp.GetRequiredService<ComplianceDbContext>());
builder.Services.AddScoped<IInboxStore>(sp => sp.GetRequiredService<ComplianceDbContext>());
builder.Services.AddScoped<IStaffDirectory, StaffDirectory>();
builder.Services.AddScoped<IComplianceCaseRepository, ComplianceCaseRepository>();
builder.Services.AddScoped<IComplianceCaseQueries, ComplianceCaseQueries>();
builder.Services.AddScoped<OpenComplianceCaseCommandHandler>();
builder.Services.AddScoped<OfficerActionCommandHandler>();
builder.Services.AddScoped<ScreenDueCaseCommandHandler>();

// ---------------------------------------------------------------- Screening provider (external)
builder.Services.AddOptions<ScreeningProviderOptions>()
    .Bind(builder.Configuration.GetSection(ScreeningProviderOptions.SectionName))
    .Validate(o => ExternalEndpoint.Problem(o.BaseUrl, o.PermittedHosts, builder.Environment.IsDevelopment()) is null,
        "ScreeningProvider:BaseUrl must be an absolute http(s) URL; outside Development https, with its host listed in ScreeningProvider:PermittedHosts and not an internal address.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "ScreeningProvider:ApiKey is not configured.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<ScreeningProviderOptions>>().Value;
    return new ScreeningRetryPolicy(TimeSpan.FromSeconds(o.RetryInitialDelaySeconds), TimeSpan.FromSeconds(o.RetryMaxDelaySeconds));
});

builder.Services
    .AddHttpClient<IScreeningProvider, ScreeningProviderClient>(ScreeningProviderClient.HttpClientName, (sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ScreeningProviderOptions>>().Value.BaseUrl))
    .WithoutRedirects()   // a 3xx never moves the call, and its API key, elsewhere
    // Resilience pipeline around every call to the external provider:
    //  - attempt timeout : a hung provider is abandoned after 5 s
    //  - retry           : 2 retries with jittered exponential back-off (screening is a
    //                      read-only check, so repeating it is safe)
    //  - circuit breaker : opens when half of at least 3 calls in 30 s fail and then
    //                      rejects calls for 30 s, so a provider outage is not hammered
    //  - total timeout   : 20 s for the whole sequence
    // What still fails leaves the case in SCREENING for a later retry - never a pass.
    .AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.UseJitter = true;
        options.CircuitBreaker.MinimumThroughput = 3;
        options.CircuitBreaker.FailureRatio = 0.5;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
    });

builder.Services.AddKeyedSingleton<LoopHeartbeat>(ScreeningWorker.HeartbeatKey);
builder.Services.AddHostedService<ScreeningWorker>();

// ---------------------------------------------------------------- Outbox relay (in-process)
// :: SECURITY :: Kafka authentication, fail closed - see AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs
builder.Services.AddKafkaOptions(builder.Configuration);   // validated at start-up
builder.Services.AddSingleton<ComplianceKafkaProducer>();
builder.Services.AddScoped<ComplianceOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(ComplianceOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<ComplianceOutboxPublisherHostedService>();
builder.Services.AddScoped<ParkedOutbox>();   // operations: list / resend parked messages

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.Compliance.Api.Controllers.ApiExceptionHandler>();

// ---------------------------------------------------------------- Health
// live : the process and its two background loops (Outbox relay, screening) are cycling;
// ready: the database answers; Degraded when Outbox rows or screenings are overdue.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .Add(new HealthCheckRegistration("outbox-relay",
        sp => new LoopHeartbeatHealthCheck(sp.GetRequiredKeyedService<LoopHeartbeat>(ComplianceOutboxPublisherHostedService.HeartbeatKey),
            "Compliance Outbox relay", TimeSpan.FromMinutes(3)),
        HealthStatus.Unhealthy, [HealthEndpoints.LiveTag]))
    .Add(new HealthCheckRegistration("screening-worker",
        sp => new LoopHeartbeatHealthCheck(sp.GetRequiredKeyedService<LoopHeartbeat>(ScreeningWorker.HeartbeatKey),
            "Screening worker", TimeSpan.FromMinutes(3)),
        HealthStatus.Unhealthy, [HealthEndpoints.LiveTag]))
    .AddDbContextCheck<ComplianceDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    .Add(new HealthCheckRegistration("outbox-backlog",
        sp => new OutboxBacklogHealthCheck(ct => ComplianceOutboxPublisher.GetBacklogAsync(sp.GetRequiredService<ComplianceDbContext>(), ct),
            TimeSpan.FromMinutes(2)),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]))
    .Add(new HealthCheckRegistration("screening-backlog",
        sp => new ScreeningBacklogHealthCheck(sp.GetRequiredService<ComplianceDbContext>()),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]));

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

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Unexpected errors become problem details with a traceId only - no stack trace, SQL or
// │ exception text reaches the caller; the details are logged under the same traceId.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseExceptionHandler();
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