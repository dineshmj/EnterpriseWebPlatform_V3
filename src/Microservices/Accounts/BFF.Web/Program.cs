using System.IdentityModel.Tokens.Jwt;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.Bff.EntityFramework;
using Duende.Bff;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Accounts.Bff.Web.Configuration;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Helpers;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

// =============================================================================
// Accounts BFF (ASP.NET Core 10, Duende BFF) - serves the Accounts MFE
// (Next.js static export in wwwroot) and is its only API. The browser holds a
// session cookie only; the officer's tokens stay in this server-side session.
// =============================================================================

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Keep the IDP's claim names as issued - no remapping to Microsoft claim types.
// └──────────────────────────────────────────────────────────────────────────────────────
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

// ┌─ :: ARCHITECTURE + SECURITY :: Accounts BFF: Backend-for-Frontend ─────────────────────────────
// │ Why : A browser app that holds OAuth tokens exposes them to every XSS bug; and a front end
// │       calling APIs directly couples the browser to every service.
// │ How : Confidential OIDC client (code + PKCE); tokens stay in the server-side session
// │       (PostgreSQL); the browser gets only a __Host- cookie; every state-changing call needs the
// │       anti-forgery header; the BFF forwards to its APIs with the person's token and holds no
// │       business rules.
// │ Ref : Features 2.1.1, 2.3, 3.3, 3.4 | IETF OAuth 2.0 for Browser-Based Apps (BFF) | OWASP A07 |
// │       ASVS V3
// └────────────────────────────────────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The IDP, this BFF's client ID and the Shell's origin (the only page that may frame it)
// │ come from configuration only - nothing compiled in; missing or not https = no start-up.
// └──────────────────────────────────────────────────────────────────────────────────────
var oidcAuthority = RequiredSetting.HttpsUrl(builder.Configuration["Oidc:Authority"], "Oidc:Authority");
var oidcClientId = RequiredSetting.Text(builder.Configuration["Oidc:ClientId"], "Oidc:ClientId");
var shellOrigin = RequiredSetting.HttpsUrl(builder.Configuration["ShellOrigin"], "ShellOrigin");

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// │ over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("accounts-bff", tracing => tracing.AddAspNetCoreInstrumentation());

builder.Services.AddOptions<AccountsBffOptions>()
    .Bind(builder.Configuration.GetSection(AccountsBffOptions.SectionName))
    .Validate(o => RequiredSetting.IsHttpsUrl(o.AccountsApiBaseUrl), $"{AccountsBffOptions.SectionName}:AccountsApiBaseUrl must be an absolute https address.")
    .ValidateOnStart();

builder.Services.AddHttpContextAccessor();
// With views: [ValidateAntiForgeryToken] needs the MVC view-features services.
builder.Services.AddControllersWithViews();
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Anti-forgery token in a header (X-CSRF-TOKEN) for every state-changing call, with its
// │ own __Host- cookie: a cross-site page can neither read nor send it.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-Accounts-Bff-CSRF";
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ HttpOnly: script never needs this cookie - the screen gets the request token from
    // │ /api/auth/csrf - so an XSS bug cannot read it either.
    // └──────────────────────────────────────────────────────────────────────────────────────
    options.Cookie.HttpOnly = true;
    // Only ever needed by same-site requests from this MFE.
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ Server-side sessions and the Data Protection keys (which encrypt the session and
// │ anti-forgery cookies) live in PostgreSQL - EwpBffStateDb, schema accounts_bff, used only by
// │ this BFF's own database user - not in memory: a restart signs nobody out, several
// │ instances share them, and back-channel logout still ends a session. Expired sessions
// │ are removed by Duende's clean-up job.
// └──────────────────────────────────────────────────────────────────────────────────────
var bffStateDb = builder.Configuration.GetConnectionString("BffStateDbConnection")
    ?? throw new InvalidOperationException("Connection string 'BffStateDbConnection' was not configured.");
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Data Protection keys in PostgreSQL, encrypted, with a FIXED application name - every
// │ instance reads the same cookies; a restart signs nobody out.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpPersistentDataProtection(bffStateDb, schema: "accounts_bff", applicationName: "ewp-accounts-bff");
builder.Services.Configure<SessionStoreOptions>(options => options.DefaultSchema = "accounts_bff");
builder.Services.AddBff()
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Server-side sessions: the cookie is only a reference, so back-channel logout can end
    // │ the session and the tokens never ride in the cookie.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddEntityFrameworkServerSideSessions(options => options.UseNpgsql(DependencyOutage.FailFast(bffStateDb)))
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
        options.Cookie.Name = "__Host-Microservice-Accounts-bff";
        options.Cookie.Path = "/";
        // Lax: never sent on cross-site sub-requests (second CSRF defence besides the
        // anti-forgery header). The Shell, this MFE and the IDP are one site, so the
        // framed MFE and the top-level IDP redirect (ResponseMode = query) still work.
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ SameSite=Lax: off cross-site sub-requests (a second CSRF defence) yet sent on the
        // │ top-level redirect back from the IDP.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ 30-minute sliding session instead of the framework's 14-day default.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    })
    .AddOpenIdConnect("oidc", options =>
    {
        options.Authority = oidcAuthority;
        options.ClientId = oidcClientId;

        // Front-channel logout (/signout-oidc) must clear THIS BFF's session cookie.
        options.SignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        // From this deployable's configuration / secret store; never compiled in.
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Client secret from configuration (Development settings or a secret store); missing =
        // │ no start-up.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"]
            ?? throw new InvalidOperationException("Oidc:ClientSecret is not configured.");
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Authorization Code + PKCE: the code is exchanged on the server with the client secret
        // │ - tokens never reach the browser.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ResponseType = "code";
        options.ResponseMode = "query";

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ PAR required (RFC 9126, FAPI 2.0): the authorization request - scopes, redirect, PKCE
        // │ challenge, prompt / acr_values - is pushed to the IDP with the client secret; the browser
        // │ is sent only a one-time request_uri. No silent fallback: an IDP without PAR stops sign-in.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Require;
        options.UsePkce = true;
        // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
        // │ Tokens kept in the server-side session for the API calls (refreshed when needed) -
        // │ never sent to the browser.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Keep the original claim names in the sign-in principal (role, branch, lan_id,
        // │ clearance_level).
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("roles");
        options.Scope.Add("organization");
        options.Scope.Add("offline_access");
        options.Scope.Add("accounts.read");
        options.Scope.Add("accounts.write");
        options.ClaimActions.MapJsonKey("role", "role", "role");
        // Display hints for the MFE (the API decides from the access token).
        options.ClaimActions.MapJsonKey("branch", "branch");
        options.ClaimActions.MapJsonKey("clearance_level", "clearance_level");
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ SameSite=None ONLY for the short-lived correlation and nonce cookies: they must
        // │ survive the cross-site round trip to the IDP (Secure, sign-in only).
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.CorrelationCookie.SameSite = SameSiteMode.None;
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SameSite = SameSiteMode.None;
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            // An API call (fetch) cannot follow a redirect to the IDP ("Failed to fetch"):
            // answer 401 instead, and the MFE signs in again silently and comes back -
            // e.g. after a restart cleared the in-memory server-side sessions.
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ If an API call (fetch) needs a new sign-in, answer 401 instead of redirecting to the
            // │ IDP - fetch cannot follow it; the screen then signs in silently and comes back.
            // └──────────────────────────────────────────────────────────────────────────────────────
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
            // Step-up (AuthController.StepUp): ask the IDP for a fresh authenticator code.
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ If this is a step-up (from /api/auth/step-up), ask the IDP for acr_values=mfa: a
            // │ fresh authenticator code, no password.
            // └──────────────────────────────────────────────────────────────────────────────────────
            if (context.Properties.Items.TryGetValue("acr_values", out var acrValues))
            {
                context.ProtocolMessage.AcrValues = acrValues;
            }
            return Task.CompletedTask;
        };
    });

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ Calls to the Accounts API carry the officer's own access token (refreshed when
// │ needed). Resilience: per-attempt and total timeouts and a circuit breaker for every
// │ call; retries for GETs only - officer actions are not idempotent, so a POST is
// │ never repeated automatically.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("AccountsApi", (serviceProvider, client) =>
    {
        var options = serviceProvider.GetRequiredService<IOptions<AccountsBffOptions>>().Value;
        client.BaseAddress = new Uri(options.AccountsApiBaseUrl);
    })
    .AddUserAccessTokenHandler()
    .AddStandardResilienceHandler(options =>
    {
        // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
        // │ Retry GETs only: a POST (a payment, a decision) is never repeated automatically - it
        // │ could act twice.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.Retry.DisableForUnsafeHttpMethods();
        options.Retry.MaxRetryAttempts = 2;
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    });

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Health endpoints for the orchestrator's probes: /health/live (process working)
// │ and /health/ready (dependencies reachable). Anonymous; no internals in the body.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag, HealthEndpoints.ReadyTag]);

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
builder.Services.AddEwpRateLimiting(builder.Configuration, "/bff", "/api");

var app = builder.Build();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ First in the pipeline: behind the ingress (TLS ends there), the original scheme and
// │ client address - only from the proxies listed under ForwardedHeaders; none listed
// │ (local runs) = no forwarded header is trusted.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpForwardedHeaders();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ One structured log line per request (Serilog), with the caller and the trace ID.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpRequestLogging();
// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ A dependency unavailable (database, IDP): 503 + Retry-After at once - a page that retries itself, or
// │ Problem Details for a call - instead of a white frame (Common.WebUtilities/Resilience).
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpOutageAnswer();

if (!app.Environment.IsDevelopment())
{
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ HSTS outside Development only: browsers then refuse plain HTTP for this host.
    // └──────────────────────────────────────────────────────────────────────────────────────
    app.UseHsts();
}

app.UseHttpsRedirection();

// Browser security headers. This MFE may be framed only by the Shell, and by the
// IDP (which loads /signout-oidc in a hidden iframe for front-channel logout).
// Full CSP: scripts limited to 'self' + the hashed inline scripts of the export
// (computed at start-up: restart this BFF after re-exporting the MFE).
var idpOrigin = ContentSecurityPolicy.Origin(oidcAuthority);
var csp = ContentSecurityPolicy.Build(
    app.Environment.WebRootPath,
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Framed only by the Shell (and the IDP during silent sign-in) - any other site framing
    // │ it is blocked (clickjacking).
    // └──────────────────────────────────────────────────────────────────────────────────────
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

app.UseEwpNextExportSegmentFiles();   // Next.js 16 export: prefetch files under the name the page asks for
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseEwpRateLimiting();
// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Duende order: UseBff after authentication and BEFORE authorization, so its
// │ anti-forgery check and endpoint metadata are evaluated first.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseBff();
app.UseAuthorization();

app.MapControllers();
app.MapBffManagementEndpoints();
app.MapEwpMfeRuntimeConfig(shellOrigin);   // /ewp-runtime-config.js: the MFE's Shell origin, set at run time
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

app.Run();