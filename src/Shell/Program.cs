using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Duende.Bff.AccessTokenManagement;
using Duende.Bff.EntityFramework;
using Duende.Bff.Yarp;
using Duende.Bff;
using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.BSS.BFFWeb.Configuration;
using EnterpriseWebPlatform.BSS.BFFWeb.Data;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Helpers;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.BSS.BFFWeb.Authentication;

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

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The IDP, the Shell's client ID and own origin, the MFEs it may frame and the
// │ Notifications API come from configuration only - nothing compiled in; a missing value,
// │ or an address that is not https, stops the start-up.
// └──────────────────────────────────────────────────────────────────────────────────────
var oidcAuthority = RequiredSetting.HttpsUrl(builder.Configuration["Oidc:Authority"], "Oidc:Authority");
var oidcClientId = RequiredSetting.Text(builder.Configuration["Oidc:ClientId"], "Oidc:ClientId");
var shellPublicOrigin = RequiredSetting.HttpsUrl(builder.Configuration["ShellOrigin"], "ShellOrigin");
// The MFEs' addresses by key: the CSP frames them and the menu links to them (MfeOrigins.cs).
var mfeOrigins = MfeOrigins.From(builder.Configuration);
builder.Services.AddSingleton(mfeOrigins);
var notificationsApiBaseUrl = RequiredSetting.HttpsUrl(builder.Configuration["NotificationsApiBaseUrl"], "NotificationsApiBaseUrl");

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Distributed tracing: W3C trace context across HTTP and Kafka; spans exported over
// │ OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
// └──────────────────────────────────────────────────────────────────────────────────────
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

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ No password in appsettings.json (it goes into the container image): Development reads
// │ the menu database's connection string from appsettings.Development.json, everywhere
// │ else from the environment. Missing = the Shell does not start (not a failure at the
// │ first menu request).
// └──────────────────────────────────────────────────────────────────────────────────────
var menuDb = builder.Configuration.GetConnectionString("MenuDbConnection")
    ?? throw new InvalidOperationException("Connection string 'MenuDbConnection' was not configured.");
builder.Services.AddDbContext<MenuDbContext>(options =>
    options.UseNpgsql(DependencyOutage.FailFast(menuDb)));

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ Server-side sessions and the Data Protection keys live in PostgreSQL (EwpBffStateDb,
// │ schema shell_bff, this BFF's own database user) - not in memory: a restart signs
// │ nobody out, several instances share them, and back-channel logout still ends a
// │ session. Duende's clean-up job removes expired sessions.
// └──────────────────────────────────────────────────────────────────────────────────────
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
    .AddEntityFrameworkServerSideSessions(options => options.UseNpgsql(DependencyOutage.FailFast(bffStateDb)))
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
        options.Cookie.Name = "__Host-BSS-Shell-bff";

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
        options.Authority = oidcAuthority;
        options.ClientId = oidcClientId;

        // Front-channel logout (/signout-oidc, called by the IDP in a hidden iframe)
        // must clear THIS BFF's session cookie. Without an explicit scheme it falls
        // back to the default sign-out scheme ("oidc") and merely redirects to the
        // IDP again, leaving the session alive.
        options.SignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ No client secret: private_key_jwt (FAPI 2.0). The PAR push and the code exchange each
        // │ carry a new one-minute assertion signed with this BFF's own key (ClientAssertions,
        // │ end of this file); refresh and revocation get theirs from the same service.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.Events.OnPushAuthorization = async context =>
        {
            var assertion = await context.HttpContext.RequestServices.GetRequiredService<IClientAssertionService>()
                .GetClientAssertionAsync(ct: context.HttpContext.RequestAborted);
            context.ProtocolMessage.ClientAssertionType = assertion!.Type;
            context.ProtocolMessage.ClientAssertion = assertion.Value;
            context.HandleClientAuthentication();
        };
        options.Events.OnAuthorizationCodeReceived = async context =>
        {
            var assertion = await context.HttpContext.RequestServices.GetRequiredService<IClientAssertionService>()
                .GetClientAssertionAsync(ct: context.HttpContext.RequestAborted);
            context.TokenEndpointRequest!.ClientAssertionType = assertion!.Type;
            context.TokenEndpointRequest.ClientAssertion = assertion.Value;
        };

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Authorization Code flow: the code is exchanged for tokens on the server, with a
        // │ client assertion and PKCE - tokens never reach the browser (no implicit or hybrid flow).
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ResponseType = "code";

        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ Response mode "query": the callback reads the code from the query string - it must
        // │ match what the IDP sends.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ResponseMode = "query";

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ PAR required (RFC 9126, FAPI 2.0): the authorization request - scopes, redirect, PKCE
        // │ challenge, prompt / acr_values - is pushed to the IDP with a client assertion; the
        // │ browser is sent only a one-time request_uri. No silent fallback: an IDP without PAR
        // │ stops sign-in.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Require;

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
        options.Scope.Add("notifications.read");
        options.Scope.Add("notifications.write");

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
// SEC: this BFF's private key, read now - missing or unreadable = no start-up (ClientAssertions).
builder.Services.AddSingleton(ClientAssertions.LoadKey(builder.Configuration));
builder.Services.AddSingleton<IClientAssertionService, ClientAssertions>();

builder.Services.AddAuthorization();
builder.Services.AddControllers();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Health endpoints for the orchestrator's probes: /health/live (process working) and
// │ /health/ready (menu database reachable). Anonymous; no internals in the body.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<MenuDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
builder.Services.AddEwpRateLimiting(builder.Configuration, "/bff");

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

if (app.Environment.IsDevelopment ())
{
	app.UseDeveloperExceptionPage ();
}
else
{
	app.UseHsts ();
}

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ A dependency unavailable (database, IDP): 503 + Retry-After at once - a page that retries itself, or
// │ Problem Details for a call - instead of a white frame (Common.WebUtilities/Resilience).
// │ AFTER the developer exception page: the inner handler sees the error first.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpOutageAnswer();

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
var shellOrigin = ContentSecurityPolicy.Origin(shellPublicOrigin);
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
        // 'self': the step-up overlay ends on the Shell's own /step-up-done.html.
        "'self'",
        // The MFEs (configuration: MfeOrigins) and the IDP.
        .. mfeOrigins.All.Select(ContentSecurityPolicy.Origin),
        ContentSecurityPolicy.Origin(oidcAuthority)
    ]);
// Security:CspReportOnly = true reports violations in the browser console instead of blocking.
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Report-only exists to observe a NEW policy before enforcing it; in normal use the CSP
// │ is enforced.
// └──────────────────────────────────────────────────────────────────────────────────────
var shellCspHeader = app.Configuration.GetValue<bool>("Security:CspReportOnly")
    ? "Content-Security-Policy-Report-Only"
    : "Content-Security-Policy";

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ ONE page may be framed - by the Shell itself only ('self'): the static step-up done
// │ page (no script, no data), where the step-up overlay ends. Every other Shell page
// │ stays never framed.
// └──────────────────────────────────────────────────────────────────────────────────────
var stepUpDoneCsp = shellCsp.Replace("frame-ancestors 'none'", "frame-ancestors 'self'");
app.Use(async (context, next) =>
{
    var isStepUpDone = context.Request.Path.Equals("/step-up-done.html", StringComparison.OrdinalIgnoreCase);
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.TryAdd(shellCspHeader, isStepUpDone ? stepUpDoneCsp : shellCsp);
        headers.TryAdd("X-Frame-Options", isStepUpDone ? "SAMEORIGIN" : "DENY");
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
app.MapRemoteBffApiEndpoint("/bff/notifications", new Uri($"{notificationsApiBaseUrl}/v1/notifications"))
    .WithAccessToken(RequiredTokenType.User);

app.MapRemoteBffApiEndpoint(NotificationsHubPath, new Uri($"{notificationsApiBaseUrl}{NotificationsHubPath}"))
    .WithAccessToken(RequiredTokenType.User)
    .SkipAntiforgery();

app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program
{
    /// <summary>The notifications hub, proxied to the Notifications API under the same path.</summary>
    private const string NotificationsHubPath = "/hubs/notifications";
}

// A block namespace: a file with top-level statements cannot have a file-scoped one.
namespace EnterpriseWebPlatform.BSS.BFFWeb.Authentication
{
    // ┌─ :: SECURITY :: private_key_jwt: this BFF's client assertions ─────────────────────────────────
    // │ Why : A shared client secret travels with every token request and the IDP knows it too;
    // │       FAPI 2.0 asks a confidential client to prove itself with an asymmetric key instead.
    // │ How : The EC P-256 private key (encrypted PKCS#8 PEM: Oidc:ClientKey:Path, its password in
    // │       Oidc:ClientKey:PasswordPath) is read once at start-up. Every call to the IDP gets a new
    // │       assertion: iss = sub = the client ID, aud = the IDP's issuer only, typ
    // │       client-authentication+jwt, 60 s, a fresh jti (the IDP takes each one once), ES256, and
    // │       kid = the RFC 7638 thumbprint New-EwpClientKeys.mjs gave the public key.
    // │ Ref : Features 2.1.1 | FAPI 2.0 Security Profile §5.3.1 | RFC 7523, 7638 |
    // │       draft-ietf-oauth-rfc7523bis | Threat-Model R-10
    // └────────────────────────────────────────────────────────────────────────────────────────────────
    internal sealed class ClientAssertions(ECDsaSecurityKey key, IOptionsMonitor<OpenIdConnectOptions> oidc, TimeProvider time)
        : IClientAssertionService
    {
        public static ECDsaSecurityKey LoadKey(IConfiguration configuration)
        {
            var keyPath = RequiredSetting.Text(configuration["Oidc:ClientKey:Path"], "Oidc:ClientKey:Path");
            var passwordPath = RequiredSetting.Text(configuration["Oidc:ClientKey:PasswordPath"], "Oidc:ClientKey:PasswordPath");
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromEncryptedPem(
                File.ReadAllText(Environment.ExpandEnvironmentVariables(keyPath)),
                File.ReadAllText(Environment.ExpandEnvironmentVariables(passwordPath)).Trim());
            if (ecdsa.ExportParameters(includePrivateParameters: false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new InvalidOperationException("The client key must be an EC P-256 key (ES256).");

            var key = new ECDsaSecurityKey(ecdsa);
            key.KeyId = Base64UrlEncoder.Encode(JsonWebKeyConverter.ConvertFromECDsaSecurityKey(key).ComputeJwkThumbprint());
            return key;
        }

        public async Task<ClientAssertion?> GetClientAssertionAsync(
            ClientCredentialsClientName? clientName = null, TokenRequestParameters? parameters = null, CancellationToken ct = default)
        {
            var options = oidc.Get("oidc");
            var idp = await options.ConfigurationManager!.GetConfigurationAsync(ct);
            var now = time.GetUtcNow().UtcDateTime;
            var assertion = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
            {
                TokenType = "client-authentication+jwt",
                Issuer = options.ClientId,
                Audience = idp.Issuer,
                Claims = new Dictionary<string, object>
                {
                    [JwtClaimTypes.Subject] = options.ClientId!,
                    [JwtClaimTypes.JwtId] = Guid.NewGuid().ToString("N")
                },
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddSeconds(60),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256)
            });
            return new ClientAssertion { Type = OidcConstants.ClientAssertionTypes.JwtBearer, Value = assertion };
        }
    }
}