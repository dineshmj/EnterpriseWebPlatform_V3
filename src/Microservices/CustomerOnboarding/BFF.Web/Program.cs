using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Duende.Bff.EntityFramework;
using Duende.Bff.Yarp;
using Duende.Bff;
using OpenTelemetry.Trace;
using Polly;

using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Configuration;
using EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.Services;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Helpers;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Keep the IDP's claim names as issued - no remapping to Microsoft claim types.
// └──────────────────────────────────────────────────────────────────────────────────────
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

// ┌─ :: ARCHITECTURE + SECURITY :: Customer Onboarding BFF: Backend-for-Frontend ──────────────────
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
builder.AddEwpObservability("customer-onboarding-bff", tracing => tracing.AddAspNetCoreInstrumentation());

builder.Services.Configure<CustomerOnboardingBffOptions>(
    builder.Configuration.GetSection(CustomerOnboardingBffOptions.SectionName));

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
// builder.Services.AddControllers();
builder.Services.AddControllersWithViews();
// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Anti-forgery token in a header (X-CSRF-TOKEN) for every state-changing call, with its
// │ own __Host- cookie: a cross-site page can neither read nor send it.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-CO-Bff-CSRF";
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
// │ anti-forgery cookies) live in PostgreSQL - EwpBffStateDb, schema customer_onboarding_bff, used only by
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
builder.AddEwpPersistentDataProtection(bffStateDb, schema: "customer_onboarding_bff", applicationName: "ewp-customer-onboarding-bff");
builder.Services.Configure<SessionStoreOptions>(options => options.DefaultSchema = "customer_onboarding_bff");
builder.Services.AddBff()
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Server-side sessions: the cookie is only a reference, so back-channel logout can end
    // │ the session and the tokens never ride in the cookie.
    // └──────────────────────────────────────────────────────────────────────────────────────
    .AddEntityFrameworkServerSideSessions(options => options.UseNpgsql(DependencyOutage.FailFast(bffStateDb)))
    .AddSessionCleanupBackgroundProcess()
    .AddRemoteApis();
builder.Services.AddOpenIdConnectAccessTokenManagement();
// SEC: this BFF's private key, read now - missing or unreadable = no start-up (ClientAssertions).
builder.Services.AddSingleton(ClientAssertions.LoadKey(builder.Configuration));
builder.Services.AddSingleton<IClientAssertionService, ClientAssertions>();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = "oidc";
        options.DefaultSignOutScheme = "oidc";
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.Name = "__Host-Microservice-Customer-Onboarding-bff";
        options.Cookie.Path = "/";
        // Lax: never sent on cross-site sub-requests (second CSRF defence besides the
        // X-CSRF header). The Shell, this MFE and the IDP are one site, so the framed
        // MFE and the top-level IDP redirect (ResponseMode = query) still work. The
        // short-lived OIDC correlation / nonce cookies below stay SameSite=None.
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

        // Front-channel logout (/signout-oidc, called by the IDP in a hidden iframe)
        // must clear THIS BFF's session cookie. Without an explicit scheme it falls
        // back to the default sign-out scheme ("oidc") and merely redirects to the
        // IDP again, leaving the session - and its stale tokens/claims - alive.
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Front-channel logout (/signout-oidc) must clear THIS BFF's cookie - otherwise it only
        // │ redirects to the IDP again and the session survives.
        // └──────────────────────────────────────────────────────────────────────────────────────
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
        // │ Authorization Code + PKCE: the code is exchanged on the server, with a client
        // │ assertion - tokens never reach the browser.
        // └──────────────────────────────────────────────────────────────────────────────────────
        options.ResponseType = "code";
        options.ResponseMode = "query";

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ PAR required (RFC 9126, FAPI 2.0): the authorization request - scopes, redirect, PKCE
        // │ challenge, prompt / acr_values - is pushed to the IDP with a client assertion; the
        // │ browser is sent only a one-time request_uri. No silent fallback: an IDP without PAR
        // │ stops sign-in.
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
        options.Scope.Add("customer-onboarding.read");
        options.Scope.Add("customer-onboarding.write");
        options.ClaimActions.MapJsonKey("role", "role", "role");
        // The user's branch: a submission stops early when the user has none (Documents
        // Management reads the branch from its own token, obtained by token exchange).
        options.ClaimActions.MapJsonKey("branch", "branch");
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
            return Task.CompletedTask;
        };
    });

builder.Services.AddSingleton<IDocumentsManagementTokenService, DocumentsManagementTokenService>();

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ Calls to the APIs, like the other .NET BFFs: a timeout per attempt and in total, a circuit
// │ breaker (a struggling API gets room to recover), and retries for GET only - creating a
// │ customer, submitting or uploading is not idempotent, so a write is never retried blindly.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("CustomerOnboardingApi", (serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CustomerOnboardingBffOptions>>().Value;
    client.BaseAddress = new Uri(options.CustomerOnboardingApiBaseUrl);
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

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ Documents Management: the same, with more time per attempt for the PDF uploads.
// └──────────────────────────────────────────────────────────────────────────────────────
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

// ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
// │ An API that is down, too slow, or behind an open circuit: a readable 503 at once, never a
// │ hanging page or a bare 500 (the submission flow handles its own failures and compensation).
// └──────────────────────────────────────────────────────────────────────────────────────
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

// Browser security headers. This MFE may be framed only by the Shell, and by
// the IDP (which loads /signout-oidc in a hidden iframe for front-channel logout).
// Full CSP: scripts limited to 'self' + the hashed inline scripts of the export.
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
//app.UseAuthorization();
// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Duende order: UseBff after authentication and BEFORE authorization, so its
// │ anti-forgery check and endpoint metadata are evaluated first.
// └──────────────────────────────────────────────────────────────────────────────────────
//app.UseBff();
app.UseBff();
app.UseAuthorization();


app.MapControllers();

app.MapBffManagementEndpoints();

app.MapEwpMfeRuntimeConfig(shellOrigin);   // /ewp-runtime-config.js: the MFE's Shell origin, set at run time
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

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