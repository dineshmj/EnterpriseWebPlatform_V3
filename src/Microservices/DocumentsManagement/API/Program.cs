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
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.DocumentsManagement.API.Authorization;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;
using EnterpriseWebPlatform.DocumentsManagement.Application;
using EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Storage;

// ┌─ :: ARCHITECTURE + SECURITY :: Documents Management API: a resource server ────────────────────
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
var clientIds = ClientIds.From(builder.Configuration).Require(nameof(ClientIds.CustomerKycWeb), nameof(ClientIds.CustomerOnboardingWeb), nameof(ClientIds.DocumentInvalidationSubscriber), nameof(ClientIds.PartnerProductsJourneyApi));
builder.Services.AddSingleton(clientIds);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// │ over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("documents-management-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

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

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString =
    builder.Configuration.GetConnectionString("DocumentsManagementDbConnection")
    ?? throw new InvalidOperationException(
        "Connection string 'DocumentsManagementDbConnection' was not configured.");

builder.Services.AddDbContext<DocumentsManagementDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddDocumentsManagementPersistence();
builder.Services.AddDocumentsManagementApplication();
builder.Services.AddSingleton<DocumentResourceAuthorization>();

var storageRoot = builder.Configuration["DocumentStorage:RootPath"];
if (string.IsNullOrWhiteSpace(storageRoot))
{
    throw new InvalidOperationException(
        "DocumentStorage:RootPath was not configured.");
}

// ┌─ SEC + DEBT ─────────────────────────────────────────────────────────────────────────
// │ Encryption at rest (envelope, AES-256-GCM): fail closed - without a valid key ring the API
// │ does not start. The Development key is in appsettings.Development.json; elsewhere the keys come
// │ from a secret store (e.g. DocumentStorage__Encryption__Keys__<id>), never from a file.
// │ Production: the key-encryption keys in a key vault or HSM (production item 1).
// └──────────────────────────────────────────────────────────────────────────────────────
var encryption = builder.Configuration.GetSection("DocumentStorage:Encryption");
var documentKeys = new DocumentKeyRing(
    encryption["CurrentKeyId"] ?? string.Empty,
    encryption.GetSection("Keys").GetChildren().ToDictionary(k => k.Key, k => k.Value ?? string.Empty, StringComparer.Ordinal));

builder.Services.AddSingleton<IDocumentStorage>(
    new EncryptedDocumentStorage(new LocalFileSystemDocumentStorage(storageRoot), documentKeys));

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
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Deny by default: an endpoint without its own policy still needs an authenticated caller.
    // :: SECURITY :: Deny by default - see Common/WebUtilities/Security/ApiEndpointAuthorization.cs
    options.DenyAnonymousByDefault();

    options.AddPolicy("ApiScope", policy =>
    {
        policy.RequireAuthenticatedUser();
    });

    options.AddPolicy("DocumentRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(
            "scope",
            "documents-management.read");
    });

    options.AddPolicy("DocumentWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(
            "scope",
            "documents-management.write");
    });

    // The Document Invalidation Subscriber's pinned machine identity: nobody else
    // may invalidate documents (saga compensation).
    options.AddPolicy("DocumentInvalidationSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(
            "scope",
            "documents-management.write");
        policy.RequireClaim(
            "client_id",
            clientIds.DocumentInvalidationSubscriber);
    });
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.DocumentsManagement.API.ErrorHandling.ApiExceptionHandler>();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Health endpoints for the orchestrator's probes: /health/live (process working)
// │ and /health/ready (dependencies reachable). Anonymous; no internals in the body.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<DocumentsManagementDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

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

app.UseHttpsRedirection();
app.UseAuthentication();
// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Order matters: authentication BEFORE the rate limiter (the budget is per person, read
// │ from the token), authorization after it.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpRateLimiting();
app.UseAuthorization();

// Deny by default: every controller endpoint needs an authenticated caller, on top of its own policy.
app.MapEwpApiControllers("ApiScope");

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

app.Run();