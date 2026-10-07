using System.IdentityModel.Tokens.Jwt;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.Bff.EntityFramework;
using Duende.Bff;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Payments.Bff.Web.Configuration;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

// =============================================================================
// Payments BFF (ASP.NET Core 10, Duende BFF) - serves the Payments MFE
// (Next.js static export in wwwroot) and is its only API. The browser holds a
// session cookie only; the staff member's tokens stay in this server-side session.
// =============================================================================

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("payments-bff", tracing => tracing.AddAspNetCoreInstrumentation());

builder.Services.Configure<PaymentsBffOptions>(builder.Configuration.GetSection(PaymentsBffOptions.SectionName));

builder.Services.AddHttpContextAccessor();
// With views: [ValidateAntiForgeryToken] needs the MVC view-features services.
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-Payments-Bff-CSRF";
    options.Cookie.HttpOnly = false;
    // Only ever needed by same-site requests from this MFE.
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Server-side sessions and the Data Protection keys (which encrypt the session and
// anti-forgery cookies) live in PostgreSQL - EwpBffStateDb, schema payments_bff, used only by
// this BFF's own database user - not in memory: a restart signs nobody out, several
// instances share them, and back-channel logout still ends a session. Expired sessions
// are removed by Duende's clean-up job.
var bffStateDb = builder.Configuration.GetConnectionString("BffStateDbConnection")
    ?? throw new InvalidOperationException("Connection string 'BffStateDbConnection' was not configured.");
builder.Services.AddEwpPersistentDataProtection(bffStateDb, schema: "payments_bff", applicationName: "ewp-payments-bff");
builder.Services.Configure<SessionStoreOptions>(options => options.DefaultSchema = "payments_bff");
builder.Services.AddBff()
    .AddEntityFrameworkServerSideSessions(options => options.UseNpgsql(bffStateDb))
    .AddSessionCleanupBackgroundProcess();
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
        options.Cookie.Name = CookieNames.MICROSERVICE_PAYMENTS_HOST_BFF;
        options.Cookie.Path = "/";
        // Lax: never sent on cross-site sub-requests (second CSRF defence besides the
        // anti-forgery header). The Shell, this MFE and the IDP are one site, so the
        // framed MFE and the top-level IDP redirect (ResponseMode = query) still work.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    })
    .AddOpenIdConnect("oidc", options =>
    {
        options.Authority = IDP.AUTHORITY;
        options.ClientId = PaymentsMicroservice.CLIENT_ID_FOR_IDP;

        // Front-channel logout (/signout-oidc) must clear THIS BFF's session cookie.
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
        options.Scope.Add(PaymentsApiScopesRequired.PAYMENTS_READ);
        options.Scope.Add(PaymentsApiScopesRequired.PAYMENTS_WRITE);
        // Read-only: the payment screen finds the customer's paying accounts in Accounts.
        options.Scope.Add(AccountsApiScopesRequired.ACCOUNTS_READ);
        options.ClaimActions.MapJsonKey("role", "role", "role");
        // Display hints for the MFE (the API decides from the access token).
        options.ClaimActions.MapJsonKey("branch", "branch");
        options.ClaimActions.MapJsonKey("clearance_level", "clearance_level");
        options.ClaimActions.MapJsonKey("lan_id", "lan_id");
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

// Calls to the Payments and Accounts APIs carry the staff member's own access token
// (refreshed when needed). Resilience: per-attempt and total timeouts and a circuit
// breaker for every call; retries for GETs only - a POST is never repeated
// automatically (the screen resends a payment with the SAME Idempotency-Key instead).
void AddApiClient(string name, Func<PaymentsBffOptions, string> baseUrl) =>
    builder.Services.AddHttpClient(name, (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<PaymentsBffOptions>>().Value;
            client.BaseAddress = new Uri(baseUrl(options));
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

AddApiClient(PaymentsBffOptions.PaymentsApiClient, o => o.PaymentsApiBaseUrl);
AddApiClient(PaymentsBffOptions.AccountsApiClient, o => o.AccountsApiBaseUrl);

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag, HealthEndpoints.ReadyTag]);

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
builder.Services.AddEwpRateLimiting(builder.Configuration, "/bff", "/api");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// Browser security headers. This MFE may be framed only by the Shell, and by the
// IDP (which loads /signout-oidc in a hidden iframe for front-channel logout).
// Full CSP: scripts limited to 'self' + the hashed inline scripts of the export
// (computed at start-up: restart this BFF after re-exporting the MFE).
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
app.UseBff();
app.UseAuthorization();

app.MapControllers();
app.MapBffManagementEndpoints();
app.MapEwpHealthEndpoints();

app.Run();