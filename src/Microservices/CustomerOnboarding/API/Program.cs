using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Application;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Monitoring;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

// ┌─ :: ARCHITECTURE + SECURITY :: Customer Onboarding API: a resource server ─────────────────────
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
var clientIds = ClientIds.From(builder.Configuration).Require(
    nameof(ClientIds.OnboardingOutcomeSubscriber),
    nameof(ClientIds.IdpCustomerDirectory));
builder.Services.AddSingleton(clientIds);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// │ over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("customer-onboarding-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ One hosting model for every .NET domain API (controllers, JWT bearer, central
// │ policies, EF Core, HTTPS), as proven in V2 - different start-up conventions per
// │ service would add operational cost, not value.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var hasBindingError = context.ModelState
                .Values
                .SelectMany(x => x.Errors)
                .Any(error =>
                    error.Exception is JsonException ||
                    error.Exception is FormatException ||
                    error.Exception is OverflowException);

            var statusCode = hasBindingError
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status422UnprocessableEntity;

            return new ObjectResult(new ValidationProblemDetails(context.ModelState)
            {
                Status = statusCode,
                Title = statusCode == StatusCodes.Status400BadRequest
                    ? "Bad Request"
                    : "Unprocessable Entity",
                Type = statusCode == StatusCodes.Status400BadRequest
                    ? "https://httpstatuses.com/400"
                    : "https://httpstatuses.com/422"
            })
            {
                StatusCode = statusCode
            };
        };
    });
builder.Services.AddHttpContextAccessor();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString =
    builder.Configuration.GetConnectionString("CustomerDbConnection")
    ?? throw new InvalidOperationException(
        "Connection string 'CustomerDbConnection' was not configured.");

builder.Services.AddDbContext<CustomerDbContext>(options =>
    options.UseNpgsql(DatabaseOutage.FailFast(connectionString)));

builder.Services.AddCustomerOnboardingPersistence();
builder.Services.AddCustomerOnboardingApplication();
builder.Services.AddScoped<CustomerResourceAuthorization>();

var authority =
    builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException(
        "Authentication:Authority was not configured.");

var audience =
    builder.Configuration["Authentication:Audience"]
    ?? throw new InvalidOperationException(
        "Authentication:Audience was not configured.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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

            RoleClaimType = "role"  // Required to explicitly tell the JWT middleware to use the "role" claim for role-based authorization
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Deny by default: an endpoint without its own policy still needs an authenticated caller.
    // :: SECURITY :: Deny by default - see Common/WebUtilities/Security/ApiEndpointAuthorization.cs
    options.DenyAnonymousByDefault();

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Scope is the coarse boundary: a valid token meant for another API never gets in. The
    // │ finer decisions (RBAC policies, branch ABAC, managing-agent ReBAC) follow per action
    // │ and in the aggregates.
    // └──────────────────────────────────────────────────────────────────────────────────────
    options.AddPolicy("ApiScope", policy =>
    {
        policy.RequireAuthenticatedUser();

        policy.RequireClaim(
            "scope",
            "customer-onboarding.read",
            "customer-onboarding.write",
            "customer-onboarding.online-identity");
    });

    // Operation policies: the client must hold the scope for THIS kind of
    // operation (a read-only token never passes a write endpoint), and the user
    // must hold an appropriate role. Object-level (branch) scope is enforced by
    // CustomerResourceAuthorization in the controllers.
    //
    // Writes are business operations: only Customer Service Agents perform them.
    // Operations and platform administrators read for support purposes but never
    // change customer data (operational authority is not business authority).
    // Auditors are deliberately NOT here: they see customers' activity through the Audit context,
    // where every look is recorded - not through the operational screens, which show personal data.
    options.AddPolicy("CustomerRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-onboarding.read");
        policy.RequireRole(
            "customer_service_agent",
            "operations_administrator",
            "platform_administrator");
    });

    options.AddPolicy("CustomerWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-onboarding.write");
        policy.RequireRole("customer_service_agent");
    });

    options.AddPolicy("OnboardingRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-onboarding.read");
        policy.RequireRole(
            "customer_service_agent",
            "operations_administrator",
            "platform_administrator");
    });

    options.AddPolicy("OnboardingWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-onboarding.write");
        policy.RequireRole("customer_service_agent");
    });

    // Internal, machine-only endpoint: workflow facts from other bounded contexts.
    // Pinned to the one M2M client that exists to deliver them.
    options.AddPolicy("OnboardingOutcomeSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-onboarding.write");
        policy.RequireClaim(
            "client_id",
            clientIds.OnboardingOutcomeSubscriber);
    });

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The customer's online registration: ONLY the IDP (its customer-directory identity,
    // │ pinned) with the scope for exactly this purpose. Staff tokens never carry that scope.
    // └──────────────────────────────────────────────────────────────────────────────────────
    options.AddPolicy("CustomerOnlineIdentity", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-onboarding.online-identity");
        policy.RequireClaim("client_id", clientIds.IdpCustomerDirectory);
    });
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.CustomerOnboarding.API.ErrorHandling.ApiExceptionHandler>();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Health endpoints for the orchestrator's probes: /health/live (process working)
// │ and /health/ready (dependencies reachable). Anonymous; no internals in the body.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<CustomerDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    .Add(new HealthCheckRegistration("onboarding-age",
        sp => ActivatorUtilities.CreateInstance<OnboardingAgeHealthCheck>(sp),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]));

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ If an alert age is missing or not a positive number of minutes, refuse to start - a
// │ silent zero would raise alarms for everything, a silent default could hide stuck work.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddOptions<OnboardingAlertOptions>()
    .Bind(builder.Configuration.GetSection(OnboardingAlertOptions.SectionName))
    .Validate(o => o.HandoverMinutes > 0 && o.OfficerDecisionMinutes > 0,
        "Onboarding:Alerts:HandoverMinutes and OfficerDecisionMinutes must be positive whole numbers of minutes.")
    .ValidateOnStart();

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
// :: SECURITY :: Per-caller rate limits - see Common/WebUtilities/Security/RateLimiting.cs
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The internal endpoints (internal/v1, called by the Kafka subscribers) are limited
// │ too, by the machine client's budget - a stolen worker secret cannot flood its API.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1", "/internal");

var app = builder.Build();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ One structured log line per request (Serilog), with the caller and the trace ID.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ HSTS outside Development only: browsers then refuse plain HTTP for this host (in
    // │ Development it would pin the developer certificate's host).
    // └──────────────────────────────────────────────────────────────────────────────────────
    app.UseHsts();
}

// Problem details without internals; see ApiExceptionHandler.
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

app.UseHttpsRedirection();
app.UseAuthentication();
// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Order matters: authentication BEFORE the rate limiter (the budget is per person, read
// │ from the token), authorization after it.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpRateLimiting();

// Remember the acting agent's LAN ID (named in published events; rules use "sub").
app.Use(async (context, next) =>
{
    await context.RequestServices.GetRequiredService<IStaffDirectory>().RememberAsync(
        context.User.FindFirst("sub")?.Value, context.User.FindFirst("lan_id")?.Value, context.RequestAborted);
    await next();
});

app.UseAuthorization();

// Deny by default: every controller endpoint needs an authenticated caller, on top of its own policy.
app.MapEwpApiControllers("ApiScope");

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

app.Run();