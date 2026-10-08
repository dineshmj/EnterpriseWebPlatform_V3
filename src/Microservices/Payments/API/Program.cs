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
using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Application.Queries;
using EnterpriseWebPlatform.Payments.Api.Authorization;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.Messaging;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.PaymentNetwork;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.Saga;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka (OTLP export when configured).
builder.AddEwpObservability("payments-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("PaymentsDbConnection")
    ?? throw new InvalidOperationException("Connection string 'PaymentsDbConnection' was not configured.");
builder.Services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(connectionString));

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
            RoleClaimType = "role"
        };
    });

// ---------------------------------------------------------------- Authorization
builder.Services.AddAuthorization(options =>
{
    // The reply courier's pinned machine identity: nobody else may hand replies to the saga.
    options.AddPolicy("PaymentsSagaReplySubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", PaymentsApiScopesRequired.PAYMENTS_WRITE);
        policy.RequireClaim("client_id",
            PaymentsMicroservice.CLIENT_ID_FOR_IDP_FOR_PAYMENTS_SAGA_REPLY_SUBSCRIBER_TO_PAYMENTS_API_M2M);
    });

    // Staff policies: scope for the kind of operation + any of the permissions + a branch.
    void AddStaffPolicy(string name, string scope, params string[] anyOfPermissions) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", scope);
            policy.AddRequirements(new PaymentsStaffRequirement(anyOfPermissions));
        });

    AddStaffPolicy("PaymentInitiate", PaymentsApiScopesRequired.PAYMENTS_WRITE, "payment.initiate");
    // Operations (workflow.view) and audit (payment.history.view) read every branch; the others their own.
    AddStaffPolicy("PaymentView", PaymentsApiScopesRequired.PAYMENTS_READ, "payment.view", "payment.initiate", "workflow.view", "payment.history.view");
    AddStaffPolicy("PaymentRetryRelease", PaymentsApiScopesRequired.PAYMENTS_WRITE, "workflow.retry");
    AddStaffPolicy("PaymentApprove", PaymentsApiScopesRequired.PAYMENTS_WRITE, "payment.approve");
    AddStaffPolicy("PaymentReject", PaymentsApiScopesRequired.PAYMENTS_WRITE, "payment.reject");
});
builder.Services.AddSingleton<IAuthorizationHandler, PaymentsStaffAuthorizationHandler>();

// ---------------------------------------------------------------- Application
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPaymentsUnitOfWork>(sp => sp.GetRequiredService<PaymentsDbContext>());
builder.Services.AddScoped<IInboxStore>(sp => sp.GetRequiredService<PaymentsDbContext>());
builder.Services.AddScoped<ISagaTrace>(sp => sp.GetRequiredService<PaymentsDbContext>());
builder.Services.AddScoped<IStaffDirectory, StaffDirectory>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IPaymentSagaRepository, PaymentSagaRepository>();
builder.Services.AddScoped<IPaymentsQueries, PaymentsQueries>();
builder.Services.AddScoped<InitiatePaymentCommandHandler>();
builder.Services.AddScoped<HandleSagaReplyCommandHandler>();
builder.Services.AddScoped<RunDueSagaStepCommandHandler>();
builder.Services.AddScoped<DecidePaymentCommandHandler>();
builder.Services.AddScoped<RetryReleaseCommandHandler>();

// The approval tier and the saga's limits are configuration, never constants in a screen.
var payments = builder.Configuration.GetSection("Payments");
var approvalThreshold = payments.GetValue<decimal?>("ApprovalThreshold") ?? 1_000m;
if (approvalThreshold <= 0)
    throw new InvalidOperationException("Payments:ApprovalThreshold must be positive.");
builder.Services.AddSingleton(new ApprovalTier(approvalThreshold));

// ABAC: what each clearance level may approve (Payments:ApprovalLimits:{level}; empty = no limit).
var limits = payments.GetSection("ApprovalLimits").GetChildren()
    .ToDictionary(
        level => int.TryParse(level.Key, out var l) ? l : throw new InvalidOperationException($"Payments:ApprovalLimits key '{level.Key}' is not a clearance level."),
        level => string.IsNullOrWhiteSpace(level.Value) ? (decimal?)null
            : decimal.Parse(level.Value, System.Globalization.CultureInfo.InvariantCulture));
if (limits.Count == 0)
    throw new InvalidOperationException("Payments:ApprovalLimits is not configured.");
builder.Services.AddSingleton(new ApprovalLimits(limits));

var saga = payments.GetSection("Saga");
var sagaPolicy = new SagaPolicy(
    TimeSpan.FromSeconds(saga.GetValue("ReplyTimeoutSeconds", 30)),
    TimeSpan.FromSeconds(saga.GetValue("MaxReplyTimeoutSeconds", 300)),
    saga.GetValue("MaxCommandAttempts", 5),
    TimeSpan.FromSeconds(saga.GetValue("NetworkRetryInitialDelaySeconds", 5)),
    TimeSpan.FromSeconds(saga.GetValue("NetworkRetryMaxDelaySeconds", 60)),
    saga.GetValue("MaxNetworkAttempts", 4));
if (sagaPolicy.ReplyTimeout <= TimeSpan.Zero || sagaPolicy.MaxCommandAttempts < 1 || sagaPolicy.MaxNetworkAttempts < 1)
    throw new InvalidOperationException("Payments:Saga limits must be positive.");
builder.Services.AddSingleton(sagaPolicy);

// ---------------------------------------------------------------- The payment network (external)
builder.Services.AddOptions<PaymentNetworkOptions>()
    .Bind(builder.Configuration.GetSection(PaymentNetworkOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "PaymentNetwork:BaseUrl is not configured.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "PaymentNetwork:ApiKey is not configured.")
    .ValidateOnStart();

builder.Services
    .AddHttpClient<IPaymentNetwork, PaymentNetworkClient>(PaymentNetworkClient.HttpClientName, (sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PaymentNetworkOptions>>().Value.BaseUrl))
    // Resilience pipeline around every call to the payment network:
    //  - attempt timeout : a hung network is abandoned after 5 s
    //  - retry           : 2 retries with jittered exponential back-off - safe for this
    //                      POST ONLY because of the Idempotency-Key (same key, same payment)
    //  - circuit breaker : opens when half of at least 3 calls in 30 s fail, then rejects
    //                      calls for 30 s, so an outage is not hammered
    //  - total timeout   : 20 s for the whole sequence
    // What still fails goes to the saga, which retries later or compensates.
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

// ---------------------------------------------------------------- Saga step runner (in-process)
builder.Services.AddKeyedSingleton<LoopHeartbeat>(SagaStepRunner.HeartbeatKey);
builder.Services.AddHostedService<SagaStepRunner>();

// ---------------------------------------------------------------- Outbox relay (in-process)
builder.Services.AddKafkaOptions(builder.Configuration);   // validated at start-up
builder.Services.AddSingleton<PaymentsKafkaProducer>();
builder.Services.AddScoped<PaymentsOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(PaymentsOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<PaymentsOutboxPublisherHostedService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.Payments.Api.Controllers.ApiExceptionHandler>();

// ---------------------------------------------------------------- Health
// live : the process and its two background loops (Outbox relay, saga step runner) are cycling;
// ready: the database answers; Degraded when Outbox rows are overdue, sagas are overdue,
//        or a compensation failed.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .Add(new HealthCheckRegistration("outbox-relay",
        sp => new LoopHeartbeatHealthCheck(sp.GetRequiredKeyedService<LoopHeartbeat>(PaymentsOutboxPublisherHostedService.HeartbeatKey),
            "Payments Outbox relay", TimeSpan.FromMinutes(3)),
        HealthStatus.Unhealthy, [HealthEndpoints.LiveTag]))
    .Add(new HealthCheckRegistration("saga-step-runner",
        sp => new LoopHeartbeatHealthCheck(sp.GetRequiredKeyedService<LoopHeartbeat>(SagaStepRunner.HeartbeatKey),
            "Payment saga step runner", TimeSpan.FromMinutes(3)),
        HealthStatus.Unhealthy, [HealthEndpoints.LiveTag]))
    .AddDbContextCheck<PaymentsDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    .Add(new HealthCheckRegistration("outbox-backlog",
        sp => new OutboxBacklogHealthCheck(ct => PaymentsOutboxPublisher.GetBacklogAsync(sp.GetRequiredService<PaymentsDbContext>(), ct),
            TimeSpan.FromMinutes(2)),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]))
    .Add(new HealthCheckRegistration("saga-backlog",
        sp => new SagaBacklogHealthCheck(sp.GetRequiredService<PaymentsDbContext>()),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]));

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1");

var app = builder.Build();

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseEwpRateLimiting();

// Remember the acting person's LAN ID (shown on screens and in events; rules use "sub").
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