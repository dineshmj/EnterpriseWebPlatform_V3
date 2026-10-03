using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.CustomerOnboarding.API.Authorization;
using EnterpriseWebPlatform.CustomerOnboarding.Application;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

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
    options.AddPolicy("CustomerRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ);
        policy.RequireRole(
            "customer_service_agent",
            "operations_administrator",
            "auditor",
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
            "auditor",
            "platform_administrator");
    });

    options.AddPolicy("OnboardingWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE);
        policy.RequireRole("customer_service_agent");
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