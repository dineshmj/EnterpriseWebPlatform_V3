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
using EnterpriseWebPlatform.DocumentsManagement.API.Authorization;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;
using EnterpriseWebPlatform.DocumentsManagement.Application;
using EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
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

builder.Services.AddSingleton<IDocumentStorage>(
    new LocalFileSystemDocumentStorage(storageRoot));

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
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiScope", policy =>
    {
        policy.RequireAuthenticatedUser();
    });

    options.AddPolicy("DocumentRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(
            "scope",
            DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_READ);
    });

    options.AddPolicy("DocumentWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(
            "scope",
            DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE);
    });

    // The Document Invalidation Subscriber's pinned machine identity: nobody else
    // may invalidate documents (saga compensation).
    options.AddPolicy("DocumentInvalidationSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(
            "scope",
            DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE);
        policy.RequireClaim(
            "client_id",
            DocumentsManagementMicroservice.CLIENT_ID_FOR_IDP_FOR_DOCUMENT_INVALIDATION_SUBSCRIBER_TO_DOC_MGMT_API_M2M);
    });
});

builder.Services.AddProblemDetails();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.DocumentsManagement.API.ErrorHandling.ApiExceptionHandler>();

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<DocumentsManagementDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

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
app.UseAuthorization();

app.MapControllers()
    .RequireAuthorization("ApiScope");

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

app.Run();