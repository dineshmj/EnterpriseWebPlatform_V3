using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Application;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("customer-onboarding-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

// WHY:
// The V2 Products API already established a proven hosting model for this PoC:
// ASP.NET Core controllers, Swagger/OpenAPI, JWT bearer authentication,
// centralized authorization, EF Core and HTTPS. V3 deliberately retains those
// aspects rather than introducing a new hosting model for every bounded context.
//
// IF NOT:
// Each V3 microservice would have different startup conventions, increasing
// operational complexity without adding architectural value.

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
    options.UseNpgsql(connectionString));

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

            RoleClaimType = "role"  // Required to explicitly tell the JWT middleware to use the "role" claim for role-based authorization
        };
    });

builder.Services.AddAuthorization(options =>
{
    // WHY:
    // Scope authorization is the coarse-grained API boundary. The finer
    // RBAC/ABAC/ReBAC decisions will be introduced at the BFF/API boundary as
    // the V3 security model is implemented.
    //
    // IF NOT:
    // A valid token intended for another resource could reach this API unless
    // every endpoint independently enforced the intended scope.
    options.AddPolicy("ApiScope", policy =>
    {
        policy.RequireAuthenticatedUser();

        policy.RequireClaim(
            "scope",
            CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ,
            CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE);
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
        policy.RequireClaim("scope", CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ);
        policy.RequireRole(
            "customer_service_agent",
            "operations_administrator",
            "platform_administrator");
    });

    options.AddPolicy("CustomerWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE);
        policy.RequireRole("customer_service_agent");
    });

    options.AddPolicy("OnboardingRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ);
        policy.RequireRole(
            "customer_service_agent",
            "operations_administrator",
            "platform_administrator");
    });

    options.AddPolicy("OnboardingWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE);
        policy.RequireRole("customer_service_agent");
    });

    // Internal, machine-only endpoint: workflow facts from other bounded contexts.
    // Pinned to the one M2M client that exists to deliver them.
    options.AddPolicy("OnboardingOutcomeSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE);
        policy.RequireClaim(
            "client_id",
            CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP_FOR_ONBOARDING_OUTCOME_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M);
    });
});

builder.Services.AddProblemDetails();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.CustomerOnboarding.API.ErrorHandling.ApiExceptionHandler>();

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<CustomerDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1");

var app = builder.Build();

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

// Problem details without internals; see ApiExceptionHandler.
app.UseExceptionHandler();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseEwpRateLimiting();

// Remember the acting agent's LAN ID (named in published events; rules use "sub").
app.Use(async (context, next) =>
{
    await context.RequestServices.GetRequiredService<IStaffDirectory>().RememberAsync(
        context.User.FindFirst("sub")?.Value, context.User.FindFirst("lan_id")?.Value, context.RequestAborted);
    await next();
});

app.UseAuthorization();

app.MapControllers()
    .RequireAuthorization("ApiScope");

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

app.Run();