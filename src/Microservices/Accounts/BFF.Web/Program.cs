using System.IdentityModel.Tokens.Jwt;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.Bff;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Accounts.Bff.Web.Configuration;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

// =============================================================================
// Accounts BFF (ASP.NET Core 10, Duende BFF) - serves the Accounts MFE
// (Next.js static export in wwwroot) and is its only API. The browser holds a
// session cookie only; the officer's tokens stay in this server-side session.
// =============================================================================

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("accounts-bff", tracing => tracing.AddAspNetCoreInstrumentation());

builder.Services.Configure<AccountsBffOptions>(builder.Configuration.GetSection(AccountsBffOptions.SectionName));

builder.Services.AddHttpContextAccessor();
// With views: [ValidateAntiForgeryToken] needs the MVC view-features services.
builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-Accounts-Bff-CSRF";
    options.Cookie.HttpOnly = false;
    // Only ever needed by same-site requests from this MFE.
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// Server-side sessions: the cookie carries only a session reference; tokens stay
// on the server and sessions can be revoked (back-channel logout). The default
// store is in-memory (sessions end on restart); use a persistent store for
// multiple instances.
builder.Services.AddBff()
    .AddServerSideSessions();
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
        options.Cookie.Name = CookieNames.MICROSERVICE_ACCOUNTS_HOST_BFF;
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
        options.ClientId = AccountsMicroservice.CLIENT_ID_FOR_IDP;

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
        options.Scope.Add(AccountsApiScopesRequired.ACCOUNTS_READ);
        options.Scope.Add(AccountsApiScopesRequired.ACCOUNTS_WRITE);
        options.ClaimActions.MapJsonKey("role", "role", "role");
        // Display hints for the MFE (the API decides from the access token).
        options.ClaimActions.MapJsonKey("branch", "branch");
        options.ClaimActions.MapJsonKey("clearance_level", "clearance_level");
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

// Calls to the Accounts API carry the officer's own access token (refreshed when
// needed). Resilience: per-attempt and total timeouts and a circuit breaker for every
// call; retries for GETs only - officer actions are not idempotent, so a POST is
// never repeated automatically.
builder.Services.AddHttpClient("AccountsApi", (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AccountsBffOptions>>().Value;
        client.BaseAddress = new Uri(options.AccountsApiBaseUrl);
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

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag, HealthEndpoints.ReadyTag]);

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
app.UseBff();
app.UseAuthorization();

app.MapControllers();
app.MapBffManagementEndpoints();
app.MapEwpHealthEndpoints();

app.Run();