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

    // Officer policies: scope for the kind of operation + role, department, clearance
    // and the operation's permission. Application-level rules live in the aggregate.
    void AddOfficerPolicy(string name, string scope, params string[] permissions) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", scope);
            policy.AddRequirements(new AccountOfficerRequirement(permissions));
        });

    AddOfficerPolicy("AccountApplicationView", AccountsApiScopesRequired.ACCOUNTS_READ, "account.application.view");
    AddOfficerPolicy("AccountApplicationReview", AccountsApiScopesRequired.ACCOUNTS_WRITE, "account.application.review");
    AddOfficerPolicy("AccountApplicationApprove", AccountsApiScopesRequired.ACCOUNTS_WRITE, "account.application.approve");
    AddOfficerPolicy("AccountApplicationReject", AccountsApiScopesRequired.ACCOUNTS_WRITE, "account.application.reject");
    AddOfficerPolicy("AccountApplicationHold", AccountsApiScopesRequired.ACCOUNTS_WRITE, "account.application.hold");
    AddOfficerPolicy("AccountView", AccountsApiScopesRequired.ACCOUNTS_READ, "account.lifecycle.view");
});
builder.Services.AddSingleton<IAuthorizationHandler, AccountOfficerAuthorizationHandler>();

// ---------------------------------------------------------------- Application
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IAccountsUnitOfWork>(sp => sp.GetRequiredService<AccountsDbContext>());
builder.Services.AddScoped<IInboxStore>(sp => sp.GetRequiredService<AccountsDbContext>());
builder.Services.AddScoped<IAccountApplicationRepository, AccountApplicationRepository>();
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IAccountsQueries, AccountsQueries>();
builder.Services.AddScoped<OpenAccountApplicationCommandHandler>();
builder.Services.AddScoped<OfficerActionCommandHandler>();
builder.Services.AddScoped<OpenDueAccountCommandHandler>();

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

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Deny by default: every controller endpoint needs an authenticated caller, on top of its own policy.
app.MapControllers().RequireAuthorization();
app.MapEwpHealthEndpoints();

await app.RunAsync();

public partial class Program { }