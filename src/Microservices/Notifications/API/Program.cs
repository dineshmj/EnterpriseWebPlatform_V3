using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Duende.AspNetCore.Authentication.JwtBearer.DPoP;
using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.Notifications.Api.Application;
using EnterpriseWebPlatform.Notifications.Api.Hubs;
using EnterpriseWebPlatform.Notifications.Api.Infrastructure;

// ┌─ :: ARCHITECTURE + SECURITY :: Notifications API: a resource server ───────────────────────────
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
var clientIds = ClientIds.From(builder.Configuration).Require(nameof(ClientIds.NotificationsSubscriber));
builder.Services.AddSingleton(clientIds);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: W3C trace context across HTTP and Kafka (OTLP export when configured).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("notifications-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("NotificationsDbConnection")
    ?? throw new InvalidOperationException("Connection string 'NotificationsDbConnection' was not configured.");
builder.Services.AddDbContext<NotificationsDbContext>(o => o.UseNpgsql(DependencyOutage.FailFast(connectionString)));

// ---------------------------------------------------------------- Authentication
// One scheme for REST and the hub: the Shell BFF proxies both and adds the person's
// access token as an Authorization header (also on the WebSocket upgrade), so the
// token never travels in a query string and never reaches the browser.
var authority = builder.Configuration["Authentication:Authority"]
    ?? throw new InvalidOperationException("Authentication:Authority was not configured.");
var audience = builder.Configuration["Authentication:Audience"]
    ?? throw new InvalidOperationException("Authentication:Audience was not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ EVERY token must be DPoP-bound (cnf), a person's or a machine's: every EWP client -
        // │ the web apps, the Journey APIs, the Kafka subscribers, the Partner Products Worker
        // │ and the IDP's own customer directory - must use DPoP, so a token without cnf came
        // │ from elsewhere (copied, or an unbound client) and is refused.
        // │ Authentication:BearerPersonTokenClients names the only exceptions: in Development,
        // │ Bruno (API testing, cannot sign proofs); elsewhere none.
        // └──────────────────────────────────────────────────────────────────────────────────────
        var bearerPersonTokenClients = builder.Configuration.GetSection("Authentication:BearerPersonTokenClients").Get<string[]>() ?? [];
        options.Events = new JwtBearerEvents
        {
            // Duende's DPoP checks (ConfigureDPoPTokensForScheme below) are chained around this event, not replacing it.
            OnTokenValidated = context =>
            {
                var principal = context.Principal!;
                if (!principal.HasClaim(c => c.Type == "cnf") &&
                    !bearerPersonTokenClients.Contains(principal.FindFirst("client_id")?.Value))
                {
                    context.Fail("The token must be DPoP-bound (cnf).");
                }
                return Task.CompletedTask;
            }
        };
    });

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ DPoP (RFC 9449, FAPI 2.0): a token bound to a key (cnf.jkt - every EWP client's)
// │ passes only as "Authorization: DPoP" with a fresh proof signed by that key, for this
// │ very method and address, carrying the token's hash (ath), at most ~30 s old (5 s +
// │ 25 s clock skew) and used once. The same bound token sent as Bearer is refused, so a
// │ copied token is useless without the key. The "Bearer" scheme itself is accepted only
// │ while Authentication:BearerPersonTokenClients names an exception (Bruno, Development);
// │ elsewhere every call must say "DPoP".
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.ConfigureDPoPTokensForScheme(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.AllowBearerTokens = builder.Configuration.GetSection("Authentication:BearerPersonTokenClients").Get<string[]>() is { Length: > 0 };
    options.ProofTokenLifetime = TimeSpan.FromSeconds(5);
    options.ProofTokenIssuedAtClockSkew = TimeSpan.FromSeconds(25);
});
// ┌─ DEBT ───────────────────────────────────────────────────────────────────────────────
// │ The proofs seen (replay cache) are kept in THIS instance's memory: with several
// │ instances, a captured proof could be replayed once on another one within its ~30 s.
// │ Production: a shared cache (HybridCache over Redis / PostgreSQL), no code change here.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddKeyedHybridCache(ServiceProviderKeys.ProofTokenReplayHybridCache);

// ---------------------------------------------------------------- Authorization
builder.Services.AddAuthorization(options =>
{
    // Deny by default: an endpoint without its own policy still needs an authenticated caller.
    // :: SECURITY :: Deny by default - see Common/WebUtilities/Security/ApiEndpointAuthorization.cs
    options.DenyAnonymousByDefault();

    // A person reading (and receiving live) their own notifications.
    options.AddPolicy("NotificationsRead", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "notifications.read");
        policy.RequireClaim("sub");
    });

    // A person marking their own notifications read.
    options.AddPolicy("NotificationsWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "notifications.write");
        policy.RequireClaim("sub");
    });

    // The subscriber's pinned machine identity: nobody else may publish notifications.
    options.AddPolicy("NotificationsSubscriberWrite", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("scope", "notifications.write");
        policy.RequireClaim("client_id",
            clientIds.NotificationsSubscriber);
    });
});

// ---------------------------------------------------------------- Application
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PublishFromEventHandler>();
builder.Services.AddScoped<NotificationQueries>();
builder.Services.AddSingleton<INotificationPusher, SignalRNotificationPusher>();

// ┌─ DEBT ───────────────────────────────────────────────────────────────────────────────
// │ SignalR: one instance here. Several instances need a backplane (Redis, or Azure
// │ SignalR Service) so a push reaches connections held by any instance (production item
// │ 9).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Clients send nothing but pings: a 4 KB message limit removes a large-payload denial
    // │ of service.
    // └──────────────────────────────────────────────────────────────────────────────────────
    options.MaximumReceiveMessageSize = 4 * 1024;   // clients send nothing but pings
});

// OWASP API4 for the live channel: a capped number of connections per person.
builder.Services.AddSingleton(new HubConnectionLimiter(
    builder.Configuration.GetValue("Notifications:MaxConnectionsPerPerson", 10)));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<EnterpriseWebPlatform.Notifications.Api.Controllers.ApiExceptionHandler>();

// ---------------------------------------------------------------- Health
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<NotificationsDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

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

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Unexpected errors become problem details with a traceId only - no stack trace, SQL or
// │ exception text reaches the caller; the details are logged under the same traceId.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseExceptionHandler();
// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ A dependency unavailable (database, IDP): 503 + Retry-After at once - a page that retries itself, or
// │ Problem Details for a call - instead of a white frame (Common.WebUtilities/Resilience).
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpOutageAnswer();
if (!app.Environment.IsDevelopment())
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ HSTS outside Development only: browsers then refuse plain HTTP for this host (in
    // │ Development it would pin the developer certificate's host).
    // └──────────────────────────────────────────────────────────────────────────────────────
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Order matters: authentication BEFORE the rate limiter (the budget is per person, read
// │ from the token), authorization after it.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpRateLimiting();
app.UseAuthorization();

// Deny by default: every endpoint needs an authenticated caller, on top of its own policy.
app.MapEwpApiControllers();
// A connection never outlives its access token: when the token expires the hub closes it,
// and the client reconnects through the Shell BFF, which supplies a fresh token.
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ CloseOnAuthenticationExpiration: the WebSocket ends with the access token - no
// │ connection outlives the sign-in.
// └──────────────────────────────────────────────────────────────────────────────────────
app.MapHub<NotificationsHub>(NotificationsHub.Path, options => options.CloseOnAuthenticationExpiration = true)
    .RequireAuthorization("NotificationsRead");
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();