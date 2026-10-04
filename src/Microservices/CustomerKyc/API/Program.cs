using EnterpriseWebPlatform.Common.Observability;
using OpenTelemetry.Trace;
using Npgsql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.AssignKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.DecideVerificationStage;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.OpenKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Queries;
using EnterpriseWebPlatform.CustomerKyc.Api.Authorization;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("customer-kyc-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("KycDbConnection")
    ?? throw new InvalidOperationException("Connection string 'KycDbConnection' was not configured.");
builder.Services.AddDbContext<KycDbContext>(o => o.UseNpgsql(connectionString));

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

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("KycCaseOpeningSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.write");
        policy.RequireClaim(
            "client_id",
            CustomerKycMicroservice.CLIENT_ID_FOR_IDP_FOR_KYC_CASE_OPENING_SUBSCRIBER_TO_CUST_KYC_API_M2M);
    });

    options.AddPolicy("KycCaseView", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.read");
        policy.RequireClaim("role", "kyc_officer");
        policy.RequireClaim("permission", "kyc.case.view");
        policy.RequireClaim("department", "KYC");
    });

    // Stage decisions: the decision permission AND the stage permission.
    void AddDecisionPolicy(string name, params string[] permissions) =>
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", "customer-kyc.write");
            policy.AddRequirements(new KycCaseDecisionRequirement(permissions));
        });

    AddDecisionPolicy("KycIdentityApprove", "kyc.case.approve", "kyc.identity.verify");
    AddDecisionPolicy("KycIdentityReject", "kyc.case.reject", "kyc.identity.verify");
    AddDecisionPolicy("KycDocumentApprove", "kyc.case.approve", "kyc.document.verify");
    AddDecisionPolicy("KycDocumentReject", "kyc.case.reject", "kyc.document.verify");

    // Claiming / releasing a case changes its assignment (ReBAC relationship).
    AddDecisionPolicy("KycCaseAssign", "kyc.case.update");
});

builder.Services.AddSingleton<IAuthorizationHandler, KycCaseDecisionAuthorizationHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IKycUnitOfWork>(sp => sp.GetRequiredService<KycDbContext>());
builder.Services.AddScoped<IInboxStore>(sp => sp.GetRequiredService<KycDbContext>());
builder.Services.AddScoped<IKycCaseRepository, KycCaseRepository>();
builder.Services.AddScoped<IKycCaseQueries, KycCaseQueries>();
builder.Services.AddScoped<OpenKycCaseCommandHandler>();
builder.Services.AddScoped<DecideVerificationStageCommandHandler>();
builder.Services.AddScoped<AssignKycCaseCommandHandler>();
builder.Services.AddSingleton<KycKafkaProducer>();
builder.Services.AddScoped<KycOutboxPublisher>();
builder.Services.AddHostedService<KycOutboxPublisherHostedService>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.CustomerKyc.Api.Controllers.ApiExceptionHandler>();

var app = builder.Build();

// Problem details without internals; see ApiExceptionHandler.
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();

public partial class Program { }