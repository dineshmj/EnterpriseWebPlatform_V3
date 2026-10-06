using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Notifications.Api.Application;
using EnterpriseWebPlatform.Notifications.Api.Hubs;
using EnterpriseWebPlatform.Notifications.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka (OTLP export when configured).
builder.AddEwpObservability("notifications-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("NotificationsDbConnection")
    ?? throw new InvalidOperationException("Connection string 'NotificationsDbConnection' was not configured.");
builder.Services.AddDbContext<NotificationsDbContext>(o => o.UseNpgsql(connectionString));

// ---------------------------------------------------------------- Authentication
// One scheme for REST and the hub: the Shell BFF proxies both and adds the person's
// access token as an Authorization header (also on the WebSocket upgrade), so the
// token never travels in a query string and never reaches the browser.
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
    // A person reading (and receiving live) their own notifications.
    options.AddPolicy("NotificationsRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", NotificationsApiScopesRequired.NOTIFICATIONS_READ);
        policy.RequireClaim("sub");
    });

    // A person marking their own notifications read.
    options.AddPolicy("NotificationsWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", NotificationsApiScopesRequired.NOTIFICATIONS_WRITE);
        policy.RequireClaim("sub");
    });

    // The subscriber's pinned machine identity: nobody else may publish notifications.
    options.AddPolicy("NotificationsSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", NotificationsApiScopesRequired.NOTIFICATIONS_WRITE);
        policy.RequireClaim("client_id",
            NotificationsMicroservice.CLIENT_ID_FOR_IDP_FOR_NOTIFICATIONS_SUBSCRIBER_TO_NOTIFICATIONS_API_M2M);
    });
});

// ---------------------------------------------------------------- Application
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PublishFromEventHandler>();
builder.Services.AddScoped<NotificationQueries>();
builder.Services.AddSingleton<INotificationPusher, SignalRNotificationPusher>();

// SignalR: one instance here. Several instances need a backplane (Redis, or Azure
// SignalR Service) so a push reaches connections held by any instance.
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    options.MaximumReceiveMessageSize = 4 * 1024;   // clients send nothing but pings
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.Notifications.Api.Controllers.ApiExceptionHandler>();

// ---------------------------------------------------------------- Health
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<NotificationsDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Deny by default: every endpoint needs an authenticated caller, on top of its own policy.
app.MapControllers().RequireAuthorization();
// A connection never outlives its access token: when the token expires the hub closes it,
// and the client reconnects through the Shell BFF, which supplies a fresh token.
app.MapHub<NotificationsHub>(NotificationsHub.Path, options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization("NotificationsRead");
app.MapEwpHealthEndpoints();

await app.RunAsync();

public partial class Program { }