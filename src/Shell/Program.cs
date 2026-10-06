using System.IdentityModel.Tokens.Jwt;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.Bff;
using Duende.Bff.AccessTokenManagement;
using Duende.Bff.Yarp;
using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.BSS.BFFWeb.Data;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

var builder = WebApplication.CreateBuilder(args);

// Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
// over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt).
builder.AddEwpObservability("shell-bff", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

JwtSecurityTokenHandler.DefaultMapInboundClaims = false;
	// 🡡__ WHY   : Prevents the JwtSecurityTokenHandler from remapping standard JWT claim types
	//              (e.g. "sub" -> ClaimTypes.NameIdentifier). Keeping the original claim names ensures
	//              consistent claim handling between IdentityServer, the OIDC middleware and downstream APIs.
	// 🡡__ IF NOT: The framework will remap JWT claim types to Microsoft-specific claim type names which can
	//              confuse token->claim mapping, cause mismatches when the application expects raw JWT claim
	//              names (like "sub" or "role"), and break authorization checks that rely on the canonical names.

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
	// 🡡__ WHY   : Session provides a server-side store for short-lived user data (e.g. temp state, correlation IDs,
	//              or small UI session values) and works with the BFF pattern to keep per-user state across requests.
	//				NOTE: In this BFF scenario, session is primarily used by the Duende BFF library to manage user sessions.
	//              The current logic written by the developer in this BFF does not directly use session, per se.
	// 🡡__ IF NOT: Without session, any logic that expects per-user server-side state via ISession will fail. Also
	//              certain middlewares or libraries that rely on session being present may throw or lose state.
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.Services.AddDbContext<MenuDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("MenuDbConnection")));

builder.Services.AddBff()
	// 🡡__ WHY   : Registers the Duende BFF services which implement the Backend-For-Frontend pattern helpers,
	//              such as secure cookie-based user sessions, CSRF protection, and the lightweight API proxy helpers.
	// 🡡__ IF NOT: You would miss out on built-in BFF features (session handling, secure cookie patterns, anti-CSRF)
	//              and have to implement these aspects manually which increases security and implementation risk.
    .AddServerSideSessions()
		// 🡡__ WHY   : Keeps the authentication ticket (and its tokens) on the server; the browser cookie holds only a
		//              session reference, and a session can be revoked server-side (e.g. on back-channel logout).
		// 🡡__ IF NOT: The tokens travel inside the (encrypted) cookie on every request and cannot be revoked centrally.
		//              NOTE: the default store is in-memory (sessions end when the BFF restarts); use a persistent store
		//              (Duende EF store / distributed cache) when the BFF runs as more than one instance.
    .AddRemoteApis();
		// 🡡__ WHY   : Enables the BFF's remote API integration (YARP-backed) allowing the BFF to expose proxied endpoints
		//              that securely call backend microservices on behalf of the authenticated user.
		// 🡡__ IF NOT: You'd either need to implement proxying yourself or have clients call microservices directly,
		//              exposing tokens to the browser and removing the benefits of the BFF pattern.

builder.Services.AddScoped<IMenuRepository, MenuRepository>();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "oidc";
			// 🡡__ WHY   : Sets the default challenge to the named OpenID Connect scheme so that
			//              unauthenticated requests trigger an OIDC authentication challenge (redirect to the IDP).
			// 🡡__ IF NOT: The system might fall back to a different challenge (or none), causing authentication flows
			//              to not redirect correctly to the identity provider and resulting in 401s or confusing behavior.
        options.DefaultSignOutScheme = "oidc";
			// 🡡__ WHY   : Ensures sign-out operations use the OIDC sign-out flow (so the user is logged out at the IDP).
			// 🡡__ IF NOT: Sign-out may only clear the local cookie without notifying the IDP, leaving SSO sessions active
			//              at the Identity Provider and causing stale sessions or surprising UX.
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = CookieNames.BSS_SHELL_HOST_BFF;

        // Same lifetime as the MFE BFF sessions (30 minutes, sliding) instead of
        // the 14-day framework default.
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;

        options.Cookie.Path = "/";
			// 🡡__ WHY   : Ensures the cookie is sent for requests to all paths of the host, including the BFF proxy and SPA.
			//              This is important when the app serves multiple endpoints under different routes.
			// 🡡__ IF NOT: A more restrictive path would prevent the cookie from being included on some requests, breaking
			//              authentication for those routes (unexpected 401s).
        
        options.Cookie.SameSite = SameSiteMode.Lax;
			// 🡡__ WHY   : The session cookie is only needed on requests from the platform's own site (the Shell, its
			//              MFEs and the IDP share one registrable domain). Lax keeps it off cross-site sub-requests,
			//              a second CSRF defence besides Duende's X-CSRF header, while still allowing the top-level
			//              redirect back from the IDP (ResponseMode = query). The short-lived OIDC correlation and
			//              nonce cookies below stay SameSite=None for the login round trip.
			// 🡡__ IF NOT: With None, the browser would attach the session to requests initiated by any other site.

        options.Cookie.HttpOnly = true;
			// 🡡__ WHY   : Prevents JavaScript from reading the cookie (mitigates XSS theft).
			// 🡡__ IF NOT: The cookie becomes accessible to client-side scripts, increasing the risk of token/session theft
			//              through XSS vulnerabilities.
        
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
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"]
            ?? throw new InvalidOperationException("Oidc:ClientSecret is not configured.");

        options.ResponseType = "code";
			// 🡡__ WHY   : The authorization code response type enforces the Authorization Code flow where the server
			//              exchanges a code for tokens — suitable for confidential BFF-style clients and more secure.
			// 🡡__ IF NOT: Using implicit or token-based response types would expose tokens to the browser and increase
			//              the risk of interception and XSS-based token theft.

        options.ResponseMode = "query";
			// 🡡__ WHY   : Instructs the IDP to return the authorization response parameters in the query string for the OIDC callback,
			//              matching how the client expects to receive the authorization code.
			// 🡡__ IF NOT: A mismatch in response mode could cause the client to not find the authorization code (e.g., if the IDP returned it in form post).

        options.MapInboundClaims = false;
			// 🡡__ WHY   : Keeps claim names from being remapped by the middleware, preserving original JWT/OIDC claim names for consistency.
			// 🡡__ IF NOT: The handler will remap claim names to Microsoft-centric names which may break authorization logic expecting raw claim types.
        
        options.ClaimActions.MapJsonKey("role", "role", "role");
        options.ClaimActions.MapJsonKey("branch", "branch");
        options.ClaimActions.MapJsonKey("branch_city", "branch_city");
        options.ClaimActions.MapJsonKey("lan_id", "lan_id");
			// 🡡__ WHY   : Ensures the "role" claim from the UserInfo JSON payload or token is mapped into the principal so Role-based
			//              authorization works as expected within ASP.NET (and so TokenValidationParameters.RoleClaimType aligns).
			// 🡡__ IF NOT: Role information may be omitted from the created ClaimsPrincipal, causing role-based checks to fail.
        
        options.SaveTokens = true;
			// 🡡__ WHY   : Persists the access and refresh tokens in the authentication ticket so the BFF can use them to call APIs.
			// 🡡__ IF NOT: Tokens would not be available via the authentication properties making it harder for the server to call
			//              downstream APIs on behalf of the user (you'd need to implement manual token handling).

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

        options.CorrelationCookie.SameSite = SameSiteMode.None;
			// 🡡__ WHY   : Correlation cookies are used to tie the outgoing authentication request to the incoming response.
			//              SameSite=None ensures these cookies participate in cross-site OIDC redirects and callback flows.
			// 🡡__ IF NOT: The correlation cookie may be blocked on cross-site redirects, breaking CSRF protections and invalidating callbacks.
        
        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
			// 🡡__ WHY   : Ensures the correlation cookie is only sent over HTTPS, preventing exposure over plaintext HTTP.
			// 🡡__ IF NOT: The cookie could be transmitted over insecure channels and be vulnerable to interception.

        options.NonceCookie.SameSite = SameSiteMode.None;
			// 🡡__ WHY   : The nonce cookie is used to prevent token replay. SameSite=None allows it to be sent during the OIDC callback.
			// 🡡__ IF NOT: The nonce cookie may be blocked during the callback, causing the middleware to reject the response due to missing nonce.

        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
            // 🡡__ WHY   : Ensures the nonce cookie is only sent over HTTPS, reducing risk of interception.
			// 🡡__ IF NOT: Nonce could be leaked over HTTP and attackers could attempt replay attacks.

        options.GetClaimsFromUserInfoEndpoint = true;
			// 🡡__ WHY   : Fetches additional user claims from the UserInfo endpoint (useful if the ID token doesn't contain all claims).
			//              This is particularly useful when the IDP returns minimal claims in the ID token but exposes more via UserInfo.
			// 🡡__ IF NOT: You may have an incomplete ClaimsPrincipal (missing profile or custom claims) and may need to request more claims
			//               via scopes or rely solely on what the ID token contains.

        options.TokenValidationParameters.NameClaimType = "name";
			// 🡡__ WHY   : Tell the token validator which claim to use as the principal's name so ASP.NET identity APIs (User.Identity.Name)
			//              work as expected.
			// 🡡__ IF NOT: The framework might pick a different claim type as the name which can lead to unexpected values in User.Identity.Name.

        options.TokenValidationParameters.RoleClaimType = "role";
			// 🡡__ WHY   : Aligns role-based checks with the "role" claim emitted by the IDP so Authorize(Role=...) works correctly.
			// 🡡__ IF NOT: The runtime may look for a different claim type for roles and role checks will fail even if "role" claims are present.
    });

builder.Services.AddOpenIdConnectAccessTokenManagement ();

builder.Services.AddAuthorization();
builder.Services.AddControllers();

// Health endpoints for the orchestrator's probes: /health/live (process working)
// and /health/ready (dependencies reachable). Anonymous; no internals in the body.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<MenuDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

if (app.Environment.IsDevelopment ())
{
	app.UseDeveloperExceptionPage ();
}
else
{
	app.UseHsts ();
}

app.UseSession();
	// 🡡__ WHY   : Ensures the session middleware is part of the pipeline so server-side session values (ISession) are available to handlers.
	// 🡡__ IF NOT: Calls to HttpContext.Session will throw or return uninitialized data and any code relying on session state will fail.

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

        if (!sameOrigin)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }

    await next();
});
	// 🡡__ WHY   : Redirects plain HTTP requests to HTTPS to guarantee transport security for cookies and token exchanges.
	// 🡡__ IF NOT: Sensitive data (cookies, tokens) could be transmitted over plaintext HTTP and be intercepted or modified.

// Browser security headers - registered BEFORE the static files, so the exported
// pages themselves (index.html …) carry them. The Shell is the top-level host and
// must never be framed (clickjacking). It frames the MFEs, and an MFE's frame
// briefly navigates to the IDP during its silent sign-in, so both are frame sources.
var shellCsp = ContentSecurityPolicy.Build(
    app.Environment.WebRootPath,
    frameAncestors: ["'none'"],
    frameSources:
    [
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.CustomerKycMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.ComplianceMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.AccountsMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo.PaymentsMicroservice.BFF_CLIENT_BASE_URL),
        ContentSecurityPolicy.Origin(IDP.AUTHORITY)
    ]);
// Security:CspReportOnly = true reports violations in the browser console instead of blocking.
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
// Duende BFF order: UseBff() after authentication and BEFORE authorization, so
// the BFF anti-forgery and endpoint metadata are evaluated first.
app.UseBff();
app.UseAuthorization();
	// 🡡__ WHY   : Enables BFF middleware which integrates authentication, anti-forgery, and proxy helpers into the pipeline.
    //               It wires up the route protection and the server-side token/session management used by remote API calls.
	// 🡡__ IF NOT: BFF-specific features (secure API proxy, built-in CSRF protections, BFF session helpers) won't run and
    //               your app would behave like a plain web app without the BFF security model.

// Protect all controllers by default except LogoutAllController.
app.MapControllers()
    .RequireAuthorization();
		// 🡡__ WHY   : Makes all controller endpoints require an authenticated user by default, enforcing a secure-by-default posture.
		// 🡡__ IF NOT: Controllers would be publicly accessible unless individually protected, increasing the risk of accidental exposure.

// LogoutAll action method must be public and hence it is not protected by default authorization.
app.MapControllerRoute(
    name: "logout-all",
    pattern: "bff/logout-all/{action=Index}",
    defaults: new { controller = "LogoutAll" }
);

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
	// 🡡__ WHY   : Registers management endpoints used by the BFF (e.g., to inspect or administrate remote API mappings, token
    //               management, and health checks). These are useful for development and operational diagnostics.
	// 🡡__ IF NOT: You will lack the BFF management endpoints which can make debugging and runtime diagnostics harder;
    //               however, consider restricting or disabling these in production if they expose sensitive operations.

app.MapEwpHealthEndpoints();

app.Run();

public partial class Program
{
    /// <summary>The notifications hub, proxied to the Notifications API under the same path.</summary>
    private const string NotificationsHubPath = "/hubs/notifications";
}