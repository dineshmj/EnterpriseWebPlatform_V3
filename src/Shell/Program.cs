using System.IdentityModel.Tokens.Jwt;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.Bff.AccessTokenManagement;
using Duende.Bff.EntityFramework;
using Duende.Bff.Yarp;
using Duende.Bff;
using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.BSS.BFFWeb.Data;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ :: ARCHITECTURE + SECURITY :: Shell BFF: a business-neutral host behind a BFF ────────────────
// │ Why : The Shell composes every context's MFE; if it held business logic or tokens in the
// │       browser, every context would depend on it and one XSS bug would expose everybody's
// │       tokens.
// │ How : Branding, sign-in, the menu (data, not code) and the Application Workspace only; the BFF
// │       keeps the tokens on the server and proxies the notification API; strict CSP, never
// │       framed, frames only the MFEs and the IDP.
// │ Ref : Features 1.5.1, 2.1.1, 3.3, 3.4 | OAuth 2.0 for Browser-Based Apps (BFF pattern) |
// │       Shell-Requirements.md
// └────────────────────────────────────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("shell-bff", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Keep the IDP's claim names (sub, role, branch) as issued - no remapping to Microsoft
// │ claim types; authorization reads them exactly as the IDP wrote them.
// └──────────────────────────────────────────────────────────────────────────────────────
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

builder.Services.AddDistributedMemoryCache();
// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ ASP.NET session: a small server-side store used by the Duende BFF; the Shell's own
// │ code keeps nothing in it.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddDbContext<MenuDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("MenuDbConnection")));

// Server-side sessions and the Data Protection keys (which encrypt the session and
// anti-forgery cookies) live in PostgreSQL - EwpBffStateDb, schema shell_bff, used only by
// this BFF's own database user - not in memory: a restart signs nobody out, several
// instances share them, and back-channel logout still ends a session. Expired sessions
// are removed by Duende's clean-up job.
var bffStateDb = builder.Configuration.GetConnectionString("BffStateDbConnection")
    ?? throw new InvalidOperationException("Connection string 'BffStateDbConnection' was not configured.");
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Data Protection keys in PostgreSQL, encrypted at rest, with a FIXED application name
// │ - every Shell instance must read the same cookies; a restart signs nobody out.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpPersistentDataProtection(bffStateDb, schema: "shell_bff", applicationName: "ewp-shell-bff");
builder.Services.Configure<SessionStoreOptions>(options => options.DefaultSchema = "shell_bff");

// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Duende BFF: tokens stay on the server and the browser holds only a session cookie;
// │ the X-CSRF anti-forgery check and the API proxy come with it. Without it, all of this
// │ would be hand-built.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddBff()
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Server-side sessions in PostgreSQL: the cookie is only a reference, so a session can
    // │ be revoked (back-channel logout) and survives a restart. Otherwise the tokens ride
    // │ inside the cookie and cannot be revoked.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddEntityFrameworkServerSideSessions(options => options.UseNpgsql(bffStateDb))
    .AddSessionCleanupBackgroundProcess()
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ Remote APIs: the BFF proxies calls to the microservices and adds the person's token -
    // │ the browser never calls an API directly and never sees a token.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddRemoteApis();

builder.Services.AddScoped<IMenuRepository, MenuRepository>();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Not signed in: challenge with OpenID Connect (a redirect to the IDP), never an
        // │ anonymous fallback.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.DefaultChallengeScheme = "oidc";
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Sign-out goes through OIDC, so the IDP's session ends too - not only the local cookie
        // │ (no stale SSO session).
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.DefaultSignOutScheme = "oidc";
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = CookieNames.BSS_SHELL_HOST_BFF;

        // Same lifetime as the MFE BFF sessions (30 minutes, sliding) instead of
        // the 14-day framework default.
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ 30-minute sliding session instead of the framework's 14-day default - an unattended
        // │ session ends quickly.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Path=/ (also required by the __Host- prefix): the cookie reaches the BFF proxy and
        // │ every SPA route.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.Cookie.Path = "/";
        
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ SameSite=Lax: the session cookie stays off cross-site sub-requests (a CSRF defence
        // │ beside the X-CSRF header) yet survives the top-level redirect back from the IDP. Only
        // │ the short-lived OIDC correlation / nonce cookies are SameSite=None.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.Cookie.SameSite = SameSiteMode.Lax;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ HttpOnly: JavaScript cannot read the authentication cookie, so an XSS bug cannot
        // │ steal the session.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.Cookie.HttpOnly = true;
        
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    })
    .AddOpenIdConnect("oidc", options =>
    {
        options.Authority = IDP.AUTHORITY;
        options.ClientId = BSSShellBFF.CLIENT_ID_FOR_IDP;

        // Front-channel logout (/signout-oidc, called by the IDP in a hidden iframe)
        // must clear THIS BFF's session cookie. Without an explicit scheme it falls
        // back to the default sign-out scheme ("oidc") and merely redirects to the
        // IDP again, leaving the session alive.
        options.SignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        // From this deployable's configuration / secret store; never compiled in.
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The client secret comes from configuration (Development settings or a secret store);
        // │ missing = no start-up.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"]
            ?? throw new InvalidOperationException("Oidc:ClientSecret is not configured.");

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Authorization Code flow: the code is exchanged for tokens on the server, with the
        // │ client secret and PKCE - tokens never reach the browser (no implicit or hybrid flow).
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ResponseType = "code";

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Response mode "query": the callback reads the code from the query string - it must
        // │ match what the IDP sends.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ResponseMode = "query";

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Keep the original claim names in the sign-in principal too (role, branch, lan_id) -
        // │ authorization relies on them.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.MapInboundClaims = false;
        
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Map "role" (and the display claims below) from UserInfo into the principal; without
        // │ it, role checks would silently fail.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ClaimActions.MapJsonKey("role", "role", "role");
        options.ClaimActions.MapJsonKey("branch", "branch");
        options.ClaimActions.MapJsonKey("branch_city", "branch_city");
        options.ClaimActions.MapJsonKey("lan_id", "lan_id");
        
        // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
        // │ SaveTokens: the access and refresh tokens are kept in the server-side session for the
        // │ API proxy - never sent to the browser.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.SaveTokens = true;

        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add("roles");
        // Branch and LAN ID for the welcome screen (display only).
        options.Scope.Add("organization");
        options.Scope.Add("offline_access");
        // Notifications API: the person's bell (REST) and live channel (SignalR), proxied below.
        options.Scope.Add(NotificationsApiScopesRequired.NOTIFICATIONS_READ);
        options.Scope.Add(NotificationsApiScopesRequired.NOTIFICATIONS_WRITE);

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ SameSite=None ONLY for the short-lived correlation cookie: it must survive the
        // │ cross-site round trip to the IDP, and it is what protects the callback against CSRF.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.CorrelationCookie.SameSite = SameSiteMode.None;
        
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Secure: the correlation cookie travels over HTTPS only (SameSite=None requires it).
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ SameSite=None for the nonce cookie too: the callback needs it to reject a replayed ID
        // │ token.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.NonceCookie.SameSite = SameSiteMode.None;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Secure: the nonce cookie travels over HTTPS only.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Profile and organisation claims come from the UserInfo endpoint, so the ID token
        // │ stays small.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.GetClaimsFromUserInfoEndpoint = true;

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ User.Identity.Name is the "name" claim.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.TokenValidationParameters.NameClaimType = "name";

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Role checks read the "role" claim exactly as the IDP issues it.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.TokenValidationParameters.RoleClaimType = "role";
    });

builder.Services.AddOpenIdConnectAccessTokenManagement ();

builder.Services.AddAuthorization();
builder.Services.AddControllers();

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<MenuDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
builder.Services.AddEwpRateLimiting(builder.Configuration, "/bff");

var app = builder.Build();

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

if (app.Environment.IsDevelopment ())
{
	app.UseDeveloperExceptionPage ();
}
else
{
	app.UseHsts ();
}

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Session middleware in the pipeline - the counterpart of AddSession above.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseSession();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ HTTPS only: plain HTTP is redirected (and HSTS is sent outside Development) - cookies
// │ and tokens never travel unencrypted.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseHttpsRedirection();

// Cross-site WebSocket hijacking guard for the notifications hub. A browser cannot add
// Duende's anti-forgery header to a WebSocket upgrade, so the hub route skips that check
// (below); instead, only pages of the Shell's own origin may open the connection. The
// session cookie is SameSite=Lax as well.
var shellOrigin = ContentSecurityPolicy.Origin(BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL);
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments(NotificationsHubPath))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        var sameOrigin = origin.Length > 0
            ? string.Equals(origin, shellOrigin, StringComparison.OrdinalIgnoreCase)
            : fetchSite.Length == 0 || fetchSite == "same-origin";

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If a notifications-hub connection comes from another origin, refuse it (403): a
        // │ browser cannot send the anti-forgery header on a WebSocket, so the Origin check stops
        // │ cross-site WebSocket hijacking.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (!sameOrigin)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }

    await next();
});

// Browser security headers - registered BEFORE the static files, so the exported
// pages themselves (index.html …) carry them. The Shell is the top-level host and
// must never be framed (clickjacking). It frames the MFEs, and an MFE's frame
// briefly navigates to the IDP during its silent sign-in, so both are frame sources.
var shellCsp = ContentSecurityPolicy.Build(
    app.Environment.WebRootPath,
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The Shell is the top-level page and is never framed (clickjacking); it may frame only
    // │ the MFEs and the IDP.
    // └──────────────────────────────────────────────────────────────────────────────────────
    frameAncestors: ["'none'"],
    frameSources:
    [
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.CustomerKycMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.ComplianceMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.AccountsMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.PaymentsMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.AuditMicroservice.WEB_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.OperationsMicroservice.WEB_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.PartnerProductsMicroservice.WEB_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(IDP.AUTHORITY)
    ]);
// Security:CspReportOnly = true reports violations in the browser console instead of blocking.
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Report-only exists to observe a NEW policy before enforcing it; in normal use the CSP
// │ is enforced.
// └──────────────────────────────────────────────────────────────────────────────────────
var shellCspHeader = app.Configuration.GetValue<bool>("Security:CspReportOnly")
    ? "Content-Security-Policy-Report-Only"
    : "Content-Security-Policy";

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.TryAdd(shellCspHeader, shellCsp);
        headers.TryAdd("X-Frame-Options", "DENY");
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
// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Duende BFF middleware: anti-forgery for /bff calls, the API proxy with the person's
// │ token and the server-side session handling. Its position matters (below).
// └──────────────────────────────────────────────────────────────────────────────────────
// Duende BFF order: UseBff() after authentication and BEFORE authorization, so
// the BFF anti-forgery and endpoint metadata are evaluated first.
app.UseBff();
app.UseAuthorization();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Deny by default: every controller needs a signed-in person; only LogoutAll is mapped
// │ separately, as public.
// └──────────────────────────────────────────────────────────────────────────────────────
// Protect all controllers by default except LogoutAllController.
app.MapControllers()
    .RequireAuthorization();

// LogoutAll action method must be public and hence it is not protected by default authorization.
app.MapControllerRoute(
    name: "logout-all",
    pattern: "bff/logout-all/{action=Index}",
    defaults: new { controller = "LogoutAll" }
);

// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Duende BFF management endpoints (/bff/login, /bff/logout, /bff/user, back-channel
// │ logout); /bff/user returns claims only, never tokens.
// └──────────────────────────────────────────────────────────────────────────────────────
app.MapBffManagementEndpoints();

// ---------------------------------------------------------------- Notifications (proxied)
// The Shell owns no notification logic: it forwards the person's requests, with their
// access token, to the Notifications API. The browser never sees the token.
//   /bff/notifications/...  -> GET list, POST {id}/read, POST read-all (anti-forgery header required)
//   /hubs/notifications     -> SignalR (negotiate + WebSocket), guarded by the Origin check above
app.MapRemoteBffApiEndpoint("/bff/notifications", new Uri($"{NotificationsMicroservice.MICROSERVICE_API_BASE_URL}/v1/notifications"))
    .WithAccessToken(RequiredTokenType.User);

app.MapRemoteBffApiEndpoint(NotificationsHubPath, new Uri($"{NotificationsMicroservice.MICROSERVICE_API_BASE_URL}{NotificationsHubPath}"))
    .WithAccessToken(RequiredTokenType.User)
    .SkipAntiforgery();

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

app.Run();

public partial class Program
{
    /// <summary>The notifications hub, proxied to the Notifications API under the same path.</summary>
    private const string NotificationsHubPath = "/hubs/notifications";
}