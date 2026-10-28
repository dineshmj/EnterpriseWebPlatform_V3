using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Application.Queries;
using EnterpriseWebPlatform.Accounts.Api.Authorization;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.CoreBanking;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Messaging;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.FeatureFlags;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ :: ARCHITECTURE + SECURITY :: Accounts API: a resource server ────────────────────────────────
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
var clientIds = ClientIds.From(builder.Configuration).Require(nameof(ClientIds.AccountApplicationOpeningSubscriber), nameof(ClientIds.AccountsCommandSubscriber), nameof(ClientIds.OperationsJourneyApi), nameof(ClientIds.PaymentsWeb));
builder.Services.AddSingleton(clientIds);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: W3C trace context across HTTP and Kafka (OTLP export when configured).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("accounts-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("AccountsDbConnection")
    ?? throw new InvalidOperationException("Connection string 'AccountsDbConnection' was not configured.");
builder.Services.AddDbContext<AccountsDbContext>(o => o.UseNpgsql(DependencyOutage.FailFast(connectionString)));

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

    // The subscriber's pinned machine identity: nobody else may open account applications.
    options.AddPolicy("AccountApplicationOpeningSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "accounts.write");
        policy.RequireClaim("client_id",
            clientIds.AccountApplicationOpeningSubscriber);
    });

    // The command courier's pinned machine identity: only it may apply funds commands
    // (sent by the Payments saga orchestrator through accounts.commands).
    options.AddPolicy("AccountsCommandSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "accounts.write");
        policy.RequireClaim("client_id",
            clientIds.AccountsCommandSubscriber);
    });

    // Officer policies: scope for the kind of operation + role, department, clearance
    // and the operation's permission. Application-level rules live in the aggregate.
    // Step-up (MFA): an account officer's decision (approve and open, or reject) need a sign-in with the authenticator code while "Mfa:Enabled"
    // is true (the token's amr contains "mfa"); with MFA off, nothing changes.
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Parked Outbox messages: only through the Operations Journey API, acting for an operator
    // │ (delegated token, act = that API) with outbox.view - and, to resend, workflow.reprocess
    // │ and a recent MFA sign-in. Never a screen, a machine or any other client directly.
    // └──────────────────────────────────────────────────────────────────────────────────────
    options.AddPolicy("AccountsOutboxView", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "accounts.read");
        policy.RequireClaim("permission", "outbox.view");
        policy.RequireDelegationBy(clientIds.OperationsJourneyApi);
    });
    options.AddPolicy("AccountsOutboxResend", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "accounts.write");
        policy.RequireClaim("permission", "workflow.reprocess");
        policy.RequireDelegationBy(clientIds.OperationsJourneyApi);
        policy.RequireMfa();
    });

    string[] stepUpPolicies = ["AccountApplicationApprove", "AccountApplicationReject"];

    void AddOfficerPolicy(string name, string scope, params string[] permissions) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", scope);
            policy.AddRequirements(new AccountOfficerRequirement(permissions));
            if (stepUpPolicies.Contains(name))
                policy.RequireMfa();
        });

    AddOfficerPolicy("AccountApplicationView", "accounts.read", "account.application.view");
    AddOfficerPolicy("AccountApplicationReview", "accounts.write", "account.application.review");
    AddOfficerPolicy("AccountApplicationApprove", "accounts.write", "account.application.approve");
    AddOfficerPolicy("AccountApplicationReject", "accounts.write", "account.application.reject");
    AddOfficerPolicy("AccountApplicationHold", "accounts.write", "account.application.hold");
    AddOfficerPolicy("AccountView", "accounts.read", "account.lifecycle.view");

    // Assisted payment screen (Payments BFF, staff member's own token): find a customer's
    // accounts in the staff member's branch. The branch itself is checked by the endpoint.
    options.AddPolicy("PaymentAccountLookup", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "accounts.read");
        policy.RequireClaim("permission", "payment.initiate");
        policy.RequireClaim("branch");
    });

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Cash deposit at the counter: only the Payments BFF (the counter screen, pinned by
    // │ client ID), for a person with account.deposit.record and a branch. Branch, customer
    // │ and account state are checked again by the command and the aggregate.
    // └──────────────────────────────────────────────────────────────────────────────────────
    options.AddPolicy("CashDepositRecord", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "accounts.write");
        policy.RequireClaim("permission", "account.deposit.record");
        policy.RequireClaim("branch");
        policy.RequireClaim("client_id", clientIds.PaymentsWeb);
    });
});
builder.Services.AddSingleton<IAuthorizationHandler, AccountOfficerAuthorizationHandler>();

// ---------------------------------------------------------------- Application
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IAccountsUnitOfWork>(sp => sp.GetRequiredService<AccountsDbContext>());
builder.Services.AddScoped<IInboxStore>(sp => sp.GetRequiredService<AccountsDbContext>());
builder.Services.AddScoped<IStaffDirectory, StaffDirectory>();
builder.Services.AddScoped<IOpeningTrace>(sp => sp.GetRequiredService<AccountsDbContext>());
builder.Services.AddScoped<IAccountApplicationRepository, AccountApplicationRepository>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IFundsHoldRepository, FundsHoldRepository>();
builder.Services.AddScoped<ICashDepositRepository, CashDepositRepository>();
builder.Services.AddScoped<IAccountsQueries, AccountsQueries>();
builder.Services.AddScoped<OpenAccountApplicationCommandHandler>();
builder.Services.AddScoped<OfficerActionCommandHandler>();
builder.Services.AddScoped<OpenDueAccountCommandHandler>();
builder.Services.AddScoped<RecordOpeningErrorCommandHandler>();
builder.Services.AddScoped<FundsCommandHandler>();
builder.Services.AddScoped<RecordCashDepositCommandHandler>();

// ---------------------------------------------------------------- Core banking (external)
builder.Services.AddOptions<CoreBankingOptions>()
    .Bind(builder.Configuration.GetSection(CoreBankingOptions.SectionName))
    .Validate(o => ExternalEndpoint.Problem(o.BaseUrl, o.PermittedHosts, builder.Environment.IsDevelopment()) is null,
        "CoreBanking:BaseUrl must be an absolute http(s) URL; outside Development https, with its host listed in CoreBanking:PermittedHosts and not an internal address.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "CoreBanking:ApiKey is not configured.")
    .Validate(o => o.MaxOpeningAttempts >= 1, "CoreBanking:MaxOpeningAttempts must be at least 1.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<CoreBankingOptions>>().Value;
    return new OpeningRetryPolicy(
        TimeSpan.FromSeconds(o.RetryInitialDelaySeconds), TimeSpan.FromSeconds(o.RetryMaxDelaySeconds), o.MaxOpeningAttempts);
});

builder.Services
    .AddHttpClient<ICoreBankingSystem, CoreBankingClient>(CoreBankingClient.HttpClientName, (sp, client) =>
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<CoreBankingOptions>>().Value.BaseUrl))
    .WithoutRedirects()   // a 3xx never moves the call, and its API key, elsewhere
    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
    // │ Resilience pipeline around every call to core banking:
    // │  - attempt timeout : a hung system is abandoned after 5 s
    // │  - retry           : 2 retries with jittered exponential back-off - safe for this
    // │                      POST ONLY because of the Idempotency-Key (same key, same account)
    // │  - circuit breaker : opens when half of at least 3 calls in 30 s fail and then
    // │                      rejects calls for 30 s, so an outage is not hammered
    // │  - total timeout   : 20 s for the whole sequence
    // │ What still fails leaves the application OPENING for a later retry - never "opened".
    // └──────────────────────────────────────────────────────────────────────────────────────
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

builder.Services.AddKeyedSingleton<LoopHeartbeat>(AccountOpeningWorker.HeartbeatKey);
builder.Services.AddHostedService<AccountOpeningWorker>();

// ---------------------------------------------------------------- Outbox relay (in-process)
// :: SECURITY :: Kafka authentication, fail closed - see AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs
builder.Services.AddKafkaOptions(builder.Configuration);   // validated at start-up
builder.Services.AddSingleton<AccountsKafkaProducer>();
builder.Services.AddScoped<AccountsOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(AccountsOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<AccountsOutboxPublisherHostedService>();
builder.Services.AddScoped<ParkedOutbox>();   // operations: list / resend parked messages

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.Accounts.Api.Controllers.ApiExceptionHandler>();

// ---------------------------------------------------------------- Health
// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ live : the process and its two background loops (Outbox relay, account opening) are cycling;
// │ ready: the database answers; Degraded when Outbox rows or approved openings are overdue.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .Add(new HealthCheckRegistration("outbox-relay",
        sp => new LoopHeartbeatHealthCheck(sp.GetRequiredKeyedService<LoopHeartbeat>(AccountsOutboxPublisherHostedService.HeartbeatKey),
            "Accounts Outbox relay", TimeSpan.FromMinutes(3)),
        HealthStatus.Unhealthy, [HealthEndpoints.LiveTag]))
    .Add(new HealthCheckRegistration("opening-worker",
        sp => new LoopHeartbeatHealthCheck(sp.GetRequiredKeyedService<LoopHeartbeat>(AccountOpeningWorker.HeartbeatKey),
            "Account opening worker", TimeSpan.FromMinutes(3)),
        HealthStatus.Unhealthy, [HealthEndpoints.LiveTag]))
    .AddDbContextCheck<AccountsDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    .Add(new HealthCheckRegistration("outbox-backlog",
        sp => new OutboxBacklogHealthCheck(ct => AccountsOutboxPublisher.GetBacklogAsync(sp.GetRequiredService<AccountsDbContext>(), ct),
            TimeSpan.FromMinutes(2)),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]))
    .Add(new HealthCheckRegistration("opening-backlog",
        sp => new OpeningBacklogHealthCheck(sp.GetRequiredService<AccountsDbContext>()),
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
builder.AddEwpFeatureFlags("accounts-api");

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