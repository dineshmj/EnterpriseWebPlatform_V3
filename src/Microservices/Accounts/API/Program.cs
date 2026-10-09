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
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka (OTLP export when configured).
builder.AddEwpObservability("accounts-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("AccountsDbConnection")
    ?? throw new InvalidOperationException("Connection string 'AccountsDbConnection' was not configured.");
builder.Services.AddDbContext<AccountsDbContext>(o => o.UseNpgsql(connectionString));

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
    // The subscriber's pinned machine identity: nobody else may open account applications.
    options.AddPolicy("AccountApplicationOpeningSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", AccountsApiScopesRequired.ACCOUNTS_WRITE);
        policy.RequireClaim("client_id",
            AccountsMicroservice.CLIENT_ID_FOR_IDP_FOR_ACCOUNT_APPLICATION_OPENING_SUBSCRIBER_TO_ACCOUNTS_API_M2M);
    });

    // The command courier's pinned machine identity: only it may apply funds commands
    // (sent by the Payments saga orchestrator through accounts.commands).
    options.AddPolicy("AccountsCommandSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", AccountsApiScopesRequired.ACCOUNTS_WRITE);
        policy.RequireClaim("client_id",
            AccountsMicroservice.CLIENT_ID_FOR_IDP_FOR_ACCOUNTS_COMMAND_SUBSCRIBER_TO_ACCOUNTS_API_M2M);
    });

    // Officer policies: scope for the kind of operation + role, department, clearance
    // and the operation's permission. Application-level rules live in the aggregate.
    // Step-up (MFA): an account officer's decision (approve and open, or reject) need a sign-in with the authenticator code while "Mfa:Enabled"
    // is true (the token's amr contains "mfa"); with MFA off, nothing changes.
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

    AddOfficerPolicy("AccountApplicationView", AccountsApiScopesRequired.ACCOUNTS_READ, "account.application.view");
    AddOfficerPolicy("AccountApplicationReview", AccountsApiScopesRequired.ACCOUNTS_WRITE, "account.application.review");
    AddOfficerPolicy("AccountApplicationApprove", AccountsApiScopesRequired.ACCOUNTS_WRITE, "account.application.approve");
    AddOfficerPolicy("AccountApplicationReject", AccountsApiScopesRequired.ACCOUNTS_WRITE, "account.application.reject");
    AddOfficerPolicy("AccountApplicationHold", AccountsApiScopesRequired.ACCOUNTS_WRITE, "account.application.hold");
    AddOfficerPolicy("AccountView", AccountsApiScopesRequired.ACCOUNTS_READ, "account.lifecycle.view");

    // Assisted payment screen (Payments BFF, staff member's own token): find a customer's
    // accounts in the staff member's branch. The branch itself is checked by the endpoint.
    options.AddPolicy("PaymentAccountLookup", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", AccountsApiScopesRequired.ACCOUNTS_READ);
        policy.RequireClaim("permission", "payment.initiate");
        policy.RequireClaim("branch");
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
builder.Services.AddScoped<IAccountsQueries, AccountsQueries>();
builder.Services.AddScoped<OpenAccountApplicationCommandHandler>();
builder.Services.AddScoped<OfficerActionCommandHandler>();
builder.Services.AddScoped<OpenDueAccountCommandHandler>();
builder.Services.AddScoped<RecordOpeningErrorCommandHandler>();
builder.Services.AddScoped<FundsCommandHandler>();

// Demo only: a new account starts with this balance so payments can be shown at once
// (Accounts:DemoOpeningDeposit; 0 when not configured).
var openingDeposit = builder.Configuration.GetValue<decimal?>("Accounts:DemoOpeningDeposit") ?? 0m;
if (openingDeposit < 0 || decimal.Round(openingDeposit, 2) != openingDeposit)
    throw new InvalidOperationException("Accounts:DemoOpeningDeposit must be zero or a positive amount in cents.");
builder.Services.AddSingleton(new AccountOpeningDeposit(openingDeposit));

// ---------------------------------------------------------------- Core banking (external)
builder.Services.AddOptions<CoreBankingOptions>()
    .Bind(builder.Configuration.GetSection(CoreBankingOptions.SectionName))
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "CoreBanking:BaseUrl is not configured.")
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
    // Resilience pipeline around every call to core banking:
    //  - attempt timeout : a hung system is abandoned after 5 s
    //  - retry           : 2 retries with jittered exponential back-off - safe for this
    //                      POST ONLY because of the Idempotency-Key (same key, same account)
    //  - circuit breaker : opens when half of at least 3 calls in 30 s fail and then
    //                      rejects calls for 30 s, so an outage is not hammered
    //  - total timeout   : 20 s for the whole sequence
    // What still fails leaves the application OPENING for a later retry - never "opened".
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
builder.Services.AddKafkaOptions(builder.Configuration);   // validated at start-up
builder.Services.AddSingleton<AccountsKafkaProducer>();
builder.Services.AddScoped<AccountsOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(AccountsOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<AccountsOutboxPublisherHostedService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.Accounts.Api.Controllers.ApiExceptionHandler>();

// ---------------------------------------------------------------- Health
// live : the process and its two background loops (Outbox relay, account opening) are cycling;
// ready: the database answers; Degraded when Outbox rows or approved openings are overdue.
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