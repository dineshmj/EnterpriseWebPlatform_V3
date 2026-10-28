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
using EnterpriseWebPlatform.Common.WebUtilities.FeatureFlags;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
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

// ┌─ :: ARCHITECTURE + SECURITY :: Payments API: a resource server ────────────────────────────────
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
var clientIds = ClientIds.From(builder.Configuration).Require(nameof(ClientIds.AuditJourneyApi), nameof(ClientIds.PartnerProductsWorker), nameof(ClientIds.PaymentsSagaReplySubscriber));
builder.Services.AddSingleton(clientIds);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: W3C trace context across HTTP and Kafka (OTLP export when configured).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("payments-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("PaymentsDbConnection")
    ?? throw new InvalidOperationException("Connection string 'PaymentsDbConnection' was not configured.");
builder.Services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(DependencyOutage.FailFast(connectionString)));

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

    // The reply courier's pinned machine identity: nobody else may hand replies to the saga.
    options.AddPolicy("PaymentsSagaReplySubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "payments.write");
        policy.RequireClaim("client_id",
            clientIds.PaymentsSagaReplySubscriber);
    });

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The Partner Products Worker's pinned machine identity: it may read ONE payment's
    // │ outcome by its Idempotency-Key (a gold purchase's payment) - nothing else.
    // └──────────────────────────────────────────────────────────────────────────────────────
    options.AddPolicy("PaymentOutcomeForPartnerProducts", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "payments.read");
        policy.RequireClaim("client_id", clientIds.PartnerProductsWorker);
    });

    // Staff policies: scope for the kind of operation + any of the permissions + a branch.
    // Step-up (MFA): a payments officer's decision and operations' "Retry release", "Resend" and reconciliation need a sign-in with the authenticator code while "Mfa:Enabled"
    // is true (the token's amr contains "mfa"); with MFA off, nothing changes.
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ These policies also require a RECENT MFA sign-in (step-up): approve, reject, retry
    // │ release, resend and reconcile.
    // └──────────────────────────────────────────────────────────────────────────────────────
    string[] stepUpPolicies = ["PaymentApprove", "PaymentReject", "PaymentRetryRelease", "PaymentOutboxResend", "PaymentReconcile"];

    void AddStaffPolicy(string name, string scope, params string[] anyOfPermissions) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", scope);
            policy.AddRequirements(new PaymentsStaffRequirement(anyOfPermissions));
            if (stepUpPolicies.Contains(name))
                policy.RequireMfa();
        });

    AddStaffPolicy("PaymentInitiate", "payments.write", "payment.initiate");
    // Operations (workflow.view) and audit (payment.history.view) read every branch; the others their own.
    // An auditor only through the Audit Journey API (PaymentsStaffAuthorizationHandler): never the Payments screens.
    AddStaffPolicy("PaymentView", "payments.read", "payment.view", "payment.initiate", "workflow.view", "payment.history.view");
    AddStaffPolicy("PaymentRetryRelease", "payments.write", "workflow.retry");
    // Parked Outbox messages: operations see them (outbox.view) and resend one (workflow.reprocess).
    AddStaffPolicy("PaymentOutboxView", "payments.read", "outbox.view");
    AddStaffPolicy("PaymentOutboxResend", "payments.write", "workflow.reprocess");
    // Manual reconciliation of a payment the network never confirmed (four eyes in the saga).
    AddStaffPolicy("PaymentReconcile", "payments.write", "payment.reconcile");
    AddStaffPolicy("PaymentApprove", "payments.write", "payment.approve");
    AddStaffPolicy("PaymentReject", "payments.write", "payment.reject");
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
builder.Services.AddScoped<IParkedOutbox, ParkedOutbox>();
builder.Services.AddScoped<ResendParkedMessageCommandHandler>();
builder.Services.AddScoped<ReconcileNetworkOutcomeCommandHandler>();

// The approval tier and the saga's limits are configuration, never constants in a screen.
// Financial controls fail closed: a missing or blank setting stops the start-up instead of
// silently becoming a default (or, worse, "no limit").
var payments = builder.Configuration.GetSection("Payments");
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ No approval threshold, or an unreadable approval limit, stops the start-up - the
// │ approval rules never fall back to "anyone may approve anything".
// └──────────────────────────────────────────────────────────────────────────────────────
var approvalThreshold = payments.GetValue<decimal?>("ApprovalThreshold")
    ?? throw new InvalidOperationException("Payments:ApprovalThreshold is not configured.");
if (approvalThreshold <= 0)
    throw new InvalidOperationException("Payments:ApprovalThreshold must be positive.");
builder.Services.AddSingleton(new ApprovalTier(approvalThreshold));

// ABAC: what each clearance level may approve (Payments:ApprovalLimits:{level}: an amount, or
// the deliberate word "Unlimited"; anything else - including blank - stops the start-up).
var limits = payments.GetSection("ApprovalLimits").GetChildren()
    .ToDictionary(
        level => int.TryParse(level.Key, out var l) ? l : throw new InvalidOperationException($"Payments:ApprovalLimits key '{level.Key}' is not a clearance level."),
        level => string.Equals(level.Value?.Trim(), "Unlimited", StringComparison.OrdinalIgnoreCase) ? (decimal?)null
            : decimal.TryParse(level.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var max) && max > 0 ? max
            : throw new InvalidOperationException($"Payments:ApprovalLimits:{level.Key} must be a positive amount or \"Unlimited\" (was '{level.Value}')."));
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
    saga.GetValue("MaxNetworkAttempts", 4),
    TimeSpan.FromMinutes(saga.GetValue("UnconfirmedAlertMinutes", 60)));
if (sagaPolicy.ReplyTimeout <= TimeSpan.Zero || sagaPolicy.MaxCommandAttempts < 1 || sagaPolicy.MaxNetworkAttempts < 1 ||
    sagaPolicy.UnconfirmedAlertAfter < TimeSpan.FromMinutes(1))
    throw new InvalidOperationException("Payments:Saga limits must be positive (UnconfirmedAlertMinutes at least 1).");
builder.Services.AddSingleton(sagaPolicy);

// ---------------------------------------------------------------- The payment network (external)
builder.Services.AddOptions<PaymentNetworkOptions>()
    .Bind(builder.Configuration.GetSection(PaymentNetworkOptions.SectionName))
    .Validate(o => ExternalEndpoint.Problem(o.BaseUrl, o.PermittedHosts, builder.Environment.IsDevelopment()) is null,
        "PaymentNetwork:BaseUrl must be an absolute http(s) URL; outside Development https, with its host listed in PaymentNetwork:PermittedHosts and not an internal address.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "PaymentNetwork:ApiKey is not configured.")
    .ValidateOnStart();

// RESIL: timeouts, retries and a circuit breaker per service (payments / lookups) - PaymentNetworkRegistration.cs
builder.Services.AddPaymentNetworkClient();

// ---------------------------------------------------------------- Saga step runner (in-process)
builder.Services.AddKeyedSingleton<LoopHeartbeat>(SagaStepRunner.HeartbeatKey);
builder.Services.AddHostedService<SagaStepRunner>();

// ---------------------------------------------------------------- Outbox relay (in-process)
// :: SECURITY :: Kafka authentication, fail closed - see AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs
builder.Services.AddKafkaOptions(builder.Configuration);   // validated at start-up
builder.Services.AddSingleton<PaymentsKafkaProducer>();
var outboxPolicy = new OutboxPolicy(
    builder.Configuration.GetValue("Payments:Outbox:MaxAttempts", 10),
    builder.Configuration.GetValue("Payments:Outbox:BaseDelaySeconds", 5.0));
if (outboxPolicy.MaxAttempts is < 1 or > 50 || outboxPolicy.BaseDelaySeconds is < 0.5 or > 300)
{
    throw new InvalidOperationException("Payments:Outbox needs MaxAttempts 1-50 and BaseDelaySeconds 0.5-300.");
}
builder.Services.AddSingleton(outboxPolicy);
builder.Services.AddScoped<PaymentsOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(PaymentsOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<PaymentsOutboxPublisherHostedService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.Payments.Api.Controllers.ApiExceptionHandler>();

// ---------------------------------------------------------------- Health
// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ live : the process and its two background loops (Outbox relay, saga step runner) are cycling;
// │ ready: the database answers; Degraded when Outbox rows are overdue, sagas are overdue,
// │        or a compensation failed.
// └──────────────────────────────────────────────────────────────────────────────────────
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
        sp => new OutboxBacklogHealthCheck(ct => PaymentsOutboxPublisher.GetBacklogAsync(sp.GetRequiredService<PaymentsDbContext>(), outboxPolicy.MaxAttempts, ct),
            TimeSpan.FromMinutes(2)),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]))
    .Add(new HealthCheckRegistration("saga-backlog",
        sp => new SagaBacklogHealthCheck(sp.GetRequiredService<PaymentsDbContext>(), sagaPolicy, sp.GetRequiredService<TimeProvider>()),
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

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Feature flags (OpenFeature; the BIAN /v2 release toggle): a flag file or LaunchDarkly -
// │ see Common/WebUtilities/FeatureFlags. Never a permission: a flag only switches OFF.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpFeatureFlags("payments-api");

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
// A switched-off /v2 endpoint answers 404 before anything else looks at the request.
app.UseEwpFeatureGates();
app.UseAuthentication();
// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Order matters: authentication BEFORE the rate limiter (the budget is per person, read
// │ from the token), authorization after it.
// └──────────────────────────────────────────────────────────────────────────────────────
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
app.MapEwpApiControllers();
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program { }