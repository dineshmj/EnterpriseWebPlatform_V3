using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

using EnterpriseWebPlatform.CustomerKyc.Api.Authorization;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

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
    options.AddPolicy("KycSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.write");
        policy.RequireClaim(
            "client_id",
            CustomerKycMicroservice.CLIENT_ID_FOR_IDP_FOR_CUST_KYC_SUBSCRIBER_TO_CUST_KYC_API_M2M);
    });

    options.AddPolicy("KycCaseView", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.read");
        policy.RequireClaim("role", "kyc_officer");
        policy.RequireClaim("permission", "kyc.case.view");
        policy.RequireClaim("department", "KYC");
    });

    options.AddPolicy("KycCaseApprove", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.write");
        policy.AddRequirements(new KycCaseDecisionRequirement("kyc.case.approve"));
    });

    options.AddPolicy("KycCaseReject", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "customer-kyc.write");
        policy.AddRequirements(new KycCaseDecisionRequirement("kyc.case.reject"));
    });
});

builder.Services.AddSingleton<IAuthorizationHandler, KycCaseDecisionAuthorizationHandler>();
builder.Services.AddScoped<KycCaseService>();
builder.Services.AddScoped<KycOutboxPublisher>();
builder.Services.AddHostedService<KycOutboxPublisherHostedService>();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();

public partial class Program { }
