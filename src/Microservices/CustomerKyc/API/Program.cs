using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.AssignKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.DecideVerificationStage;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.OpenKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Queries;
using EnterpriseWebPlatform.CustomerKyc.Api.Authorization;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("customer-kyc-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("KycDbConnection")
    ?? throw new InvalidOperationException("Connection string 'KycDbConnection' was not configured.");
builder.Services.AddDbContext<KycDbContext>(o => o.UseNpgsql(connectionString));

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
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("KycCaseOpeningSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.write");
        policy.RequireClaim(
            "client_id",
            CustomerKycMicroservice.CLIENT_ID_FOR_IDP_FOR_KYC_CASE_OPENING_SUBSCRIBER_TO_CUST_KYC_API_M2M);
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
    void AddDecisionPolicy(string name, params string[] permissions) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", "customer-kyc.write");
            policy.AddRequirements(new KycCaseDecisionRequirement(permissions));
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
builder.Services.AddKafkaOptions(builder.Configuration);
builder.Services.AddSingleton<KycKafkaProducer>();
builder.Services.AddScoped<KycOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(KycOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<KycOutboxPublisherHostedService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.CustomerKyc.Api.Controllers.ApiExceptionHandler>();

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
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
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1");

var app = builder.Build();

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

// Problem details without internals; see ApiExceptionHandler.
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
app.MapControllers();

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program { }