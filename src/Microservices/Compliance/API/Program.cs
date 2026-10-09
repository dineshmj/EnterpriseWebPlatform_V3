using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Application.Commands;
using EnterpriseWebPlatform.Compliance.Api.Application.Queries;
using EnterpriseWebPlatform.Compliance.Api.Authorization;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Messaging;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Screening;

var builder = WebApplication.CreateBuilder(args);

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

// ---------------------------------------------------------------- Authorization
builder.Services.AddAuthorization(options =>
{
    // The subscriber's pinned machine identity: nobody else may open cases.
    options.AddPolicy("ComplianceCaseOpeningSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", ComplianceApiScopesRequired.COMPLIANCE_WRITE);
        policy.RequireClaim("client_id",
            ComplianceMicroservice.CLIENT_ID_FOR_IDP_FOR_COMPLIANCE_CASE_OPENING_SUBSCRIBER_TO_COMPLIANCE_API_M2M);
    });

    // Officer policies: scope for the kind of operation + role, department, clearance
    // and the operation's permission. Case-level rules live in the aggregate.
    // Step-up (MFA): a compliance officer's decision (approve or reject) need a sign-in with the authenticator code while "Mfa:Enabled"
    // is true (the token's amr contains "mfa"); with MFA off, nothing changes.
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

    AddOfficerPolicy("ComplianceCaseView", ComplianceApiScopesRequired.COMPLIANCE_READ, "compliance.case.view");
    AddOfficerPolicy("ComplianceCaseReview", ComplianceApiScopesRequired.COMPLIANCE_WRITE, "compliance.case.review");
    AddOfficerPolicy("ComplianceCaseApprove", ComplianceApiScopesRequired.COMPLIANCE_WRITE, "compliance.case.approve");
    AddOfficerPolicy("ComplianceCaseReject", ComplianceApiScopesRequired.COMPLIANCE_WRITE, "compliance.case.reject");
    AddOfficerPolicy("ComplianceCaseHold", ComplianceApiScopesRequired.COMPLIANCE_WRITE, "compliance.case.hold");
    AddOfficerPolicy("ComplianceCaseReleaseHold", ComplianceApiScopesRequired.COMPLIANCE_WRITE, "compliance.case.release");
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
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "ScreeningProvider:BaseUrl is not configured.")
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
builder.Services.AddKafkaOptions(builder.Configuration);   // validated at start-up
builder.Services.AddSingleton<ComplianceKafkaProducer>();
builder.Services.AddScoped<ComplianceOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(ComplianceOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<ComplianceOutboxPublisherHostedService>();

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
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1");
builder.Services.AddEwpMfaStepUp();   // the step-up requirement and its clear 403 ("mfa_required")

var app = builder.Build();

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
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
app.MapControllers().RequireAuthorization();
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program { }