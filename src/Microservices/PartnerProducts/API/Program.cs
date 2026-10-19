using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.PartnerProducts.Api.Authorization;
using EnterpriseWebPlatform.PartnerProducts.Api.Controllers;
using EnterpriseWebPlatform.PartnerProducts.Api.Domain;
using EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure;
using EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;

// ┌─ :: ARCHITECTURE + SECURITY :: Partner Products API: a resource server ────────────────────────
// │ Why : EWP's side of the Partner Bank's Digital Gold (EWP as distributor): the customer's signed
// │       Form VII and transfer slips, and later what the Partner Bank answered. The domain API owns
// │       this data and its rules; it trusts nothing the caller says about itself.
// │ How : Validates every JWT access token (issuer, audience, signature, lifetime, at+jwt), denies
// │       by default, and accepts only the Journey API acting for a staff member with the action's
// │       permission and a branch; the domain refuses what the Partner Bank would refuse.
// │ Ref : Features 1.1.1, 1.2, 2.2 | OAuth 2.0 resource server (RFC 6750, RFC 9068) |
// │       OWASP API1 / API5 | Partner Bank repo doc/Architecture.md D14
// └────────────────────────────────────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// Traces, Serilog logs and Prometheus metrics (OTLP export when configured).
builder.AddEwpObservability("partner-products-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

var connectionString = builder.Configuration.GetConnectionString("PartnerProductsDbConnection")
    ?? throw new InvalidOperationException("Connection string 'PartnerProductsDbConnection' was not configured.");
builder.Services.AddDbContext<PartnerProductsDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The Partner Bank (an external system, Threat-Model TB6): both addresses checked at
// │ start-up (outside Development: https, permitted hosts, no internal address), EWP's keys
// │ loaded once (a missing file stops the API), mutual TLS with EWP's client certificate,
// │ the server trusted only through the PoC's own root, no redirects followed, a timeout.
// └──────────────────────────────────────────────────────────────────────────────────────
var partnerBankOptions = builder.Configuration.GetSection(PartnerBankOptions.SectionName).Get<PartnerBankOptions>()
    ?? throw new InvalidOperationException($"The '{PartnerBankOptions.SectionName}' section is missing.");
foreach (var (name, url) in new[]
{
    ("TokenEndpoint", partnerBankOptions.TokenEndpoint), ("ApiBaseUrl", partnerBankOptions.ApiBaseUrl), ("StaffViewUrl", partnerBankOptions.StaffViewUrl)
})
{
    if (ExternalEndpoint.Problem(url, partnerBankOptions.PermittedHosts, builder.Environment.IsDevelopment()) is { } problem)
        throw new InvalidOperationException($"PartnerBank:{name} {problem}.");
    if (!url.StartsWith("https://", StringComparison.Ordinal))
        throw new InvalidOperationException($"PartnerBank:{name} must be https (mutual TLS).");
}
if (string.IsNullOrWhiteSpace(partnerBankOptions.ClientId))
    throw new InvalidOperationException("PartnerBank:ClientId is required.");
var settlement = partnerBankOptions.Settlement;
if (string.IsNullOrWhiteSpace(settlement.PayeeName) || settlement.PayeeName.Length > 70 ||
    !System.Text.RegularExpressions.Regex.IsMatch(settlement.Bsb, @"^\d{3}-\d{3}$") ||
    !System.Text.RegularExpressions.Regex.IsMatch(settlement.AccountNumber, @"^\d{5,9}$"))
{
    throw new InvalidOperationException("PartnerBank:Settlement needs PayeeName, Bsb (123-456) and AccountNumber (5-9 digits): the purchases' payee.");
}
builder.Services.AddSingleton(new SettlementAccount(settlement.PayeeName.Trim(), settlement.Bsb, settlement.AccountNumber));
var partnerBankCredentials = new PartnerBankCredentials(partnerBankOptions);
builder.Services.AddSingleton(partnerBankOptions);
builder.Services.AddSingleton(partnerBankCredentials);
builder.Services.AddHttpClient<PartnerBankClient>(client => client.Timeout = TimeSpan.FromSeconds(partnerBankOptions.TimeoutSeconds))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ An unreachable Partner Bank (PC off, started without -Lan) fails in 5 s, not after
        // │ the whole timeout: the counter hears "not reachable, send again" while the callers
        // │ (Journey API, web BFF) are still waiting - not their own generic time-out.
        // └──────────────────────────────────────────────────────────────────────────────────────
        ConnectTimeout = TimeSpan.FromSeconds(5),
        SslOptions =
        {
            ClientCertificates = [partnerBankCredentials.ClientCertificate],
            RemoteCertificateValidationCallback = (_, certificate, _, errors) => partnerBankCredentials.IsTrustedServer(certificate, errors)
        }
    });

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
        // Keep claim names as issued (sub, permission, branch, lan_id, act) - the policies read them by these names.
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
    });

builder.Services.AddAuthorization(options =>
{
    // Deny by default: an endpoint without its own policy still needs an authenticated caller.
    // :: SECURITY :: Deny by default - see Common/WebUtilities/Security/ApiEndpointAuthorization.cs
    options.DenyAnonymousByDefault();
    options.AddDigitalGoldPolicies();
});

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
// :: SECURITY :: Per-caller rate limits - see Common/WebUtilities/Security/RateLimiting.cs
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1", "/internal");

// live: the process; ready: the database.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .AddDbContextCheck<PartnerProductsDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

// One structured log line per request (Serilog), with the caller and the trace ID.
app.UseEwpRequestLogging();

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Unexpected errors become problem details with a traceId only - no stack trace, SQL or
// │ exception text reaches the caller; the details are logged under the same traceId.
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
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
app.MapEwpHealthEndpoints();
app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

await app.RunAsync();

public partial class Program { }