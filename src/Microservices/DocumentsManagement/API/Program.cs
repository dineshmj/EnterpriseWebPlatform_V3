using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using EnterpriseWebPlatform.DocumentsManagement.Application;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;
using EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Storage;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

var builder = WebApplication.CreateBuilder(args);

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

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features
            .Get<IExceptionHandlerFeature>()
            ?.Error;

        var problem = new ProblemDetails
        {
            Instance = context.Request.Path
        };

        switch (exception)
        {
            case DomainRuleViolationException:
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                problem.Status = StatusCodes.Status422UnprocessableEntity;
                problem.Title = "Domain rule violation";
                problem.Detail = exception.Message;
                break;

            case KeyNotFoundException:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                problem.Status = StatusCodes.Status404NotFound;
                problem.Title = "Resource not found";
                problem.Detail = exception.Message;
                break;

            case InvalidOperationException:
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                problem.Status = StatusCodes.Status409Conflict;
                problem.Title = "Operation could not be completed";
                problem.Detail = exception.Message;
                break;

            default:
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                problem.Status = StatusCodes.Status500InternalServerError;
                problem.Title = "An unexpected error occurred";
                problem.Detail = app.Environment.IsDevelopment()
                    ? exception?.Message
                    : null;
                break;
        }

        await Results.Problem(problem).ExecuteAsync(context);
    });
});

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers()
    .RequireAuthorization("ApiScope");

app.Run();