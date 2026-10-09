using System.IdentityModel.Tokens.Jwt;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.Bff.EntityFramework;
using Duende.Bff.Yarp;
using Duende.Bff;
using OpenTelemetry.Trace;
using Polly;

using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Configuration;
using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Services;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("customer-onboarding-bff", tracing => tracing.AddAspNetCoreInstrumentation());

builder.Services.Configure<CustomerOnboardingBffOptions>(
    builder.Configuration.GetSection(CustomerOnboardingBffOptions.SectionName));

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
// builder.Services.AddControllers();
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-CO-Bff-CSRF";
    options.Cookie.HttpOnly = false;
    // Only ever needed by same-site requests from this MFE.
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Server-side sessions and the Data Protection keys (which encrypt the session and
// anti-forgery cookies) live in PostgreSQL - EwpBffStateDb, schema customer_onboarding_bff, used only by
// this BFF's own database user - not in memory: a restart signs nobody out, several
// instances share them, and back-channel logout still ends a session. Expired sessions
// are removed by Duende's clean-up job.
var bffStateDb = builder.Configuration.GetConnectionString("BffStateDbConnection")
    ?? throw new InvalidOperationException("Connection string 'BffStateDbConnection' was not configured.");
builder.AddEwpPersistentDataProtection(bffStateDb, schema: "customer_onboarding_bff", applicationName: "ewp-customer-onboarding-bff");
builder.Services.Configure<SessionStoreOptions>(options => options.DefaultSchema = "customer_onboarding_bff");
builder.Services.AddBff()
    .AddEntityFrameworkServerSideSessions(options => options.UseNpgsql(bffStateDb))
    .AddSessionCleanupBackgroundProcess()
    .AddRemoteApis();
builder.Services.AddOpenIdConnectAccessTokenManagement();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "oidc";
        options.DefaultSignOutScheme = "oidc";
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = CookieNames.MICROSERVICE_CUSTOMER_ONBOARDING_HOST_BFF;
        options.Cookie.Path = "/";
        // Lax: never sent on cross-site sub-requests (second CSRF defence besides the
        // X-CSRF header). The Shell, this MFE and the IDP are one site, so the framed
        // MFE and the top-level IDP redirect (ResponseMode = query) still work. The
        // short-lived OIDC correlation / nonce cookies below stay SameSite=None.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    })
    .AddOpenIdConnect("oidc", options =>
    {
        options.Authority = IDP.AUTHORITY;
        options.ClientId = CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP;

        // Front-channel logout (/signout-oidc, called by the IDP in a hidden iframe)
        // must clear THIS BFF's session cookie. Without an explicit scheme it falls
        // back to the default sign-out scheme ("oidc") and merely redirects to the
        // IDP again, leaving the session - and its stale tokens/claims - alive.
        options.SignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        // From this deployable's configuration / secret store; never compiled in.
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"]
            ?? throw new InvalidOperationException("Oidc:ClientSecret is not configured.");
        options.ResponseType = "code";
        options.ResponseMode = "query";
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("roles");
        options.Scope.Add("organization");
        options.Scope.Add("offline_access");
        options.Scope.Add("customer-onboarding.read");
        options.Scope.Add("customer-onboarding.write");
        options.ClaimActions.MapJsonKey("role", "role", "role");
        // The user's branch: a submission stops early when the user has none (Documents
        // Management reads the branch from its own token, obtained by token exchange).
        options.ClaimActions.MapJsonKey("branch", "branch");
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";

        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SameSite = SameSiteMode.None;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            // An API call (fetch) cannot follow a redirect to the IDP ("Failed to fetch"):
            // answer 401 instead, and the MFE signs in again silently and comes back -
            // e.g. after a restart cleared the in-memory server-side sessions.
            if (context.Request.Path.StartsWithSegments("/bff/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.HandleResponse();
                return Task.CompletedTask;
            }

            if (context.Properties.Items.TryGetValue("prompt", out var prompt))
            {
                context.ProtocolMessage.Prompt = prompt;
            }
            return Task.CompletedTask;
        };
    });

builder.Services.AddSingleton<IDocumentsManagementTokenService, DocumentsManagementTokenService>();

// Calls to the APIs, like the other .NET BFFs: a timeout per attempt and in total, a circuit
// breaker (a struggling API gets room to recover), and retries for GET only - creating a
// customer, submitting or uploading is not idempotent, so a write is never retried blindly.
builder.Services.AddHttpClient("CustomerOnboardingApi", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CustomerOnboardingBffOptions>>().Value;
    client.BaseAddress = new Uri(options.CustomerOnboardingApiBaseUrl);
})
.AddUserAccessTokenHandler()
.AddStandardResilienceHandler(options =>
{
    options.Retry.DisableForUnsafeHttpMethods();
    options.Retry.MaxRetryAttempts = 2;
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
});

// Documents Management: the same, with more time per attempt for the PDF uploads.
builder.Services.AddHttpClient("DocumentsManagementApi", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CustomerOnboardingBffOptions>>().Value;
    client.BaseAddress = new Uri(options.DocumentsManagementApiBaseUrl);
})
.AddStandardResilienceHandler(options =>
{
    options.Retry.DisableForUnsafeHttpMethods();
    options.Retry.MaxRetryAttempts = 2;
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
});

builder.Services.AddHttpClient("IdentityServerTokenClient", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CustomerOnboardingBffOptions>>().Value;
    client.BaseAddress = new Uri(options.IdentityServerAuthority);
});

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag, HealthEndpoints.ReadyTag]);

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
builder.Services.AddEwpRateLimiting(builder.Configuration, "/bff", "/api");

var app = builder.Build();

// An API that is down, too slow, or behind an open circuit: a readable 503 at once, never a
// hanging page or a bare 500 (the submission flow handles its own failures and compensation).
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (ex is ExecutionRejectedException or HttpRequestException && !context.Response.HasStarted)
    {
        context.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogWarning(ex, "A downstream API is unavailable for {Method} {Path}.", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { message = "The Customer Onboarding service is temporarily unavailable. Please try again shortly." });
    }
});

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// Browser security headers. This MFE may be framed only by the Shell, and by
// the IDP (which loads /signout-oidc in a hidden iframe for front-channel logout).
// Full CSP: scripts limited to 'self' + the hashed inline scripts of the export.
var shellOrigin = builder.Configuration["ShellOrigin"] ?? BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL;
var idpOrigin = ContentSecurityPolicy.Origin(IDP.AUTHORITY);
var csp = ContentSecurityPolicy.Build(
    app.Environment.WebRootPath,
    frameAncestors: [shellOrigin, idpOrigin],
    frameSources: []);
// Security:CspReportOnly = true reports violations in the browser console instead of blocking.
var cspHeader = app.Configuration.GetValue<bool>("Security:CspReportOnly")
    ? "Content-Security-Policy-Report-Only"
    : "Content-Security-Policy";
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.TryAdd(cspHeader, csp);
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
        return Task.CompletedTask;
    });

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseEwpRateLimiting();
//app.UseAuthorization();
//app.UseBff();
app.UseBff();
app.UseAuthorization();


app.MapControllers();

app.MapBffManagementEndpoints();

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

app.Run();