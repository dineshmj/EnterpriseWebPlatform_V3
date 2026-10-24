using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

using Npgsql;
using OpenTelemetry.Trace;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Resilience;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.PartnerProducts.Api.Authorization;
using EnterpriseWebPlatform.PartnerProducts.Api.Controllers;
using EnterpriseWebPlatform.PartnerProducts.Api.Domain;
using EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure;
using EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure.Messaging;
using EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure.Monitoring;
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

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The client IDs this API pins in its authorization rules, from its own configuration
// │ (section ClientIds; each must equal the IDP's ClientIds entry). Read now: a missing one
// │ stops the start-up - a pin is never empty.
// └──────────────────────────────────────────────────────────────────────────────────────
var clientIds = ClientIds.From(builder.Configuration).Require(
    nameof(ClientIds.PartnerProductsJourneyApi), nameof(ClientIds.PartnerProductsWorker), nameof(ClientIds.OperationsJourneyApi));
builder.Services.AddSingleton(clientIds);

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Traces, Serilog logs and Prometheus metrics (OTLP export when configured).
// └──────────────────────────────────────────────────────────────────────────────────────
builder.AddEwpObservability("partner-products-api", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

var connectionString = builder.Configuration.GetConnectionString("PartnerProductsDbConnection")
    ?? throw new InvalidOperationException("Connection string 'PartnerProductsDbConnection' was not configured.");
// The DbContext writes each change's events to the Outbox; who acted comes from the request's token.
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<PartnerProductsDbContext>(o => o.UseNpgsql(DatabaseOutage.FailFast(connectionString)));

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
        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
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
    options.AddDigitalGoldPolicies(clientIds);
});
builder.Services.AddEwpMfaStepUp();   // the step-up requirement of the Outbox resend and its clear 403 ("mfa_required")
// :: SECURITY :: Step-up MFA - see Common/WebUtilities/Security/MfaStepUp.cs
builder.RequireMfaOutsideDevelopment();   // fail closed: MFA can be off on a developer's machine only

// OWASP API4: per-caller rate limits on the API surface (429 + Retry-After); configuration "RateLimiting".
// :: SECURITY :: Per-caller rate limits - see Common/WebUtilities/Security/RateLimiting.cs
builder.Services.AddEwpRateLimiting(builder.Configuration, "/v1", "/internal");

// ---------------------------------------------------------------- Outbox relay (in-process)
// :: SECURITY :: Kafka authentication, fail closed - see AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs
builder.Services.AddKafkaOptions(builder.Configuration);   // validated at start-up
builder.Services.AddSingleton<PartnerProductsKafkaProducer>();
builder.Services.AddScoped<PartnerProductsOutboxPublisher>();
builder.Services.AddKeyedSingleton<LoopHeartbeat>(PartnerProductsOutboxPublisherHostedService.HeartbeatKey);
builder.Services.AddHostedService<PartnerProductsOutboxPublisherHostedService>();
builder.Services.AddScoped<ParkedOutbox>();   // operations: list / resend parked messages

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ If an alert age is missing, not positive, or the unpaid-order age does not exceed the
// │ Worker's own cancellation (35 min), refuse to start - a silent zero would raise alarms
// │ for everything, a too-short age would report orders the Worker is about to cancel.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddOptions<DigitalGoldAlertOptions>()
    .Bind(builder.Configuration.GetSection(DigitalGoldAlertOptions.SectionName))
    .Validate(o => o.HandoverMinutes > 0 && o.PaymentDecisionMinutes > 0 &&
                   o.UnpaidOrderMinutes > DigitalGoldAlertOptions.WorkerCancelsUnpaidWithinMinutes,
        $"DigitalGold:Alerts needs HandoverMinutes and PaymentDecisionMinutes > 0 and UnpaidOrderMinutes > {DigitalGoldAlertOptions.WorkerCancelsUnpaidWithinMinutes} (whole minutes).")
    .ValidateOnStart();

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ live: the process and its Outbox relay are cycling; ready: the database answers - Degraded
// │ when Outbox rows wait too long or are parked, or a Form VII or purchase waits too long in one step.
// └──────────────────────────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
    .Add(new HealthCheckRegistration("outbox-relay",
        sp => new LoopHeartbeatHealthCheck(sp.GetRequiredKeyedService<LoopHeartbeat>(PartnerProductsOutboxPublisherHostedService.HeartbeatKey),
            "Partner Products Outbox relay", TimeSpan.FromMinutes(3)),
        HealthStatus.Unhealthy, [HealthEndpoints.LiveTag]))
    .AddDbContextCheck<PartnerProductsDbContext>("database", tags: [HealthEndpoints.ReadyTag])
    .Add(new HealthCheckRegistration("outbox-backlog",
        sp => new OutboxBacklogHealthCheck(ct => PartnerProductsOutboxPublisher.GetBacklogAsync(sp.GetRequiredService<PartnerProductsDbContext>(), ct),
            TimeSpan.FromMinutes(2)),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]))
    .Add(new HealthCheckRegistration("digital-gold-age",
        sp => ActivatorUtilities.CreateInstance<DigitalGoldAgeHealthCheck>(sp),
        HealthStatus.Degraded, [HealthEndpoints.ReadyTag]));

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
// │ Database unreachable: 503 + Retry-After at once - a page that retries itself, or
// │ Problem Details for a call - instead of a white frame (Common.WebUtilities/Resilience).
// └──────────────────────────────────────────────────────────────────────────────────────
app.UseEwpDatabaseOutageAnswer();
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