using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.DocumentsManagement.API.Authorization;
using EnterpriseWebPlatform.DocumentsManagement.Application;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;
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
});

builder.Services.AddProblemDetails();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.DocumentsManagement.API.ErrorHandling.ApiExceptionHandler>();

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<DocumentsManagementDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

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
app.UseAuthorization();

app.MapControllers()
    .RequireAuthorization("ApiScope");

app.MapEwpHealthEndpoints();

app.Run();