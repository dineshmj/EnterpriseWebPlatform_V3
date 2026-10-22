using System.Threading.RateLimiting;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Duende.IdentityServer.EntityFramework.Stores;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
using EnterpriseWebPlatform.IdentityServer.Customers;
using EnterpriseWebPlatform.IdentityServer.Data;
using EnterpriseWebPlatform.IdentityServer.Repositories;
using EnterpriseWebPlatform.IdentityServer.Security;
using EnterpriseWebPlatform.IdentityServer.Services;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

Log.Information("Starting up");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
    // │ Distributed tracing (W3C trace context across HTTP and Kafka; OTLP export when
    // │ OTEL_EXPORTER_OTLP_ENDPOINT is set - see ReadMe.txt), Serilog logs and Prometheus
    // │ metrics.
    // └──────────────────────────────────────────────────────────────────────────────────────
    builder.AddEwpObservability("identity-server", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

    // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
    // │ The IDP's own sign-in counters (user_login, user_login_failure, user_logout, consent_*;
    // │ Pages/Telemetry.cs) live on a meter named after the assembly. A meter is exported only
    // │ when it is listed, so it is added here - without it a burst of failed sign-ins would
    // │ never reach /metrics or an alert.
    // └──────────────────────────────────────────────────────────────────────────────────────
    builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(EnterpriseWebPlatform.IdentityServer.Pages.Telemetry.ServiceName));

    // PostgreSQL database for identity and authorization data.
    var identityDb = builder.Configuration.GetConnectionString("IdentityDbConnection")
        ?? throw new InvalidOperationException("Connection string 'IdentityDbConnection' was not configured.");
    builder.Services.AddDbContext<IdentityDbContext>(options => options.UseNpgsql(identityDb));

    // The IDP's Data Protection keys (its cookies, and the encrypted columns of the operational
    // store below) live in EwpIdentityAccessDb, schema identity_server, encrypted at rest:
    // a restart or a second instance can still read them. No certificate outside Development
    // means no start-up (the keys are never stored readable).
    builder.AddEwpPersistentDataProtection(identityDb, schema: "identity_server", applicationName: "ewp-idp");

    builder.Services.AddScoped<IUserRepository, UserRepository>();
    builder.Services.AddScoped<IPasswordManager, PasswordManager>();

    // Two-step sign-in (TOTP - Google Authenticator). "Mfa:Enabled" is false by default; when
    // true it applies to EVERY user: enrolment at the next sign-in, then the code each time.
    // Off is allowed in Development only: elsewhere the IDP refuses to start without it.
    builder.Services.Configure<MfaOptions>(builder.Configuration.GetSection(MfaOptions.SectionName));
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Outside Development MFA cannot be off: the IDP refuses to start (the APIs check the
    // │ same).
    // └──────────────────────────────────────────────────────────────────────────────────────
    builder.RequireMfaOutsideDevelopment();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<MfaService>();

    // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
    // │ The customers' own sign-in ("My EWP"): their table, one-time codes, and Customer
    // │ Onboarding's answers during an online registration. Codes on the page (in place of an
    // │ SMS) only in Development - anywhere else the IDP refuses to start with it on.
    // └──────────────────────────────────────────────────────────────────────────────────────
    builder.Services.AddOptions<CustomerSignInOptions>()
        .Bind(builder.Configuration.GetSection(CustomerSignInOptions.SectionName))
        .Validate(o => !o.ShowCodesOnPage || builder.Environment.IsDevelopment(),
            "CustomerSignIn:ShowCodesOnPage is allowed in Development only.")
        .ValidateOnStart();
    var customerSignIn = builder.Configuration.GetSection(CustomerSignInOptions.SectionName).Get<CustomerSignInOptions>() ?? new();
    builder.Services.AddSingleton(ClientIds.From(builder.Configuration).Require(nameof(ClientIds.IdpCustomerDirectory)));
    builder.Services.AddScoped<CustomerLoginStore>();
    builder.Services.AddScoped<CustomerOneTimeCodes>();
    builder.Services.AddScoped<CustomerSignIn>();
    builder.Services.AddHttpClient<CustomerDirectory>(http =>
    {
        http.BaseAddress = new Uri(RequiredSetting.HttpsUrl(customerSignIn.CustomerOnboardingApiBaseUrl,
            $"{CustomerSignInOptions.SectionName}:{nameof(CustomerSignInOptions.CustomerOnboardingApiBaseUrl)}") + "/");
        http.Timeout = TimeSpan.FromSeconds(10);
    });

    // ┌─ SEC + DEBT ─────────────────────────────────────────────────────────────────────────
    // │ DPoP proofs are accepted once (RFC 9449 jti replay check): Duende's replay cache
    // │ lives in IDistributedCache - in memory for this single instance; production with
    // │ several IDP instances needs a shared one (Redis), or a proof could be replayed at
    // │ another instance within its minute (Threat-Model R-14).
    // └──────────────────────────────────────────────────────────────────────────────────────
    builder.Services.AddDistributedMemoryCache();

    builder.Services.AddRazorPages();

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Per-IP login throttling (20 a minute) on top of per-account lockout: slows password
    // │ spraying across many usernames.
    // └──────────────────────────────────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("login", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
    });

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The client list is built NOW, so a missing client secret stops the start-up instead
    // │ of failing at the first sign-in.
    // └──────────────────────────────────────────────────────────────────────────────────────
    ClientSecretStore.Initialize(builder.Configuration);
    // The public keys of the private_key_jwt clients (the customers' channel) - files, read now.
    ClientKeyStore.Initialize(builder.Configuration);
    // Client IDs and the web clients' addresses (redirect URIs) - configuration, not code.
    ClientSettings.Initialize(builder.Configuration);
    var clients = Config.GetClients(builder.Environment.IsDevelopment()).ToList();

    builder.Services
        .AddIdentityServer(options =>
        {
            // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
            // │ IdentityServer's events (sign-in success and failure, tokens issued, errors) go to
            // │ the log: the IDP's own security trail, with the trace ID of the request.
            // └──────────────────────────────────────────────────────────────────────────────────────
            options.Events.RaiseErrorEvents = true;
            options.Events.RaiseInformationEvents = true;
            options.Events.RaiseFailureEvents = true;
            options.Events.RaiseSuccessEvents = true;

            // Server-side sessions (below): the session's display name for administration, and an
            // expired IDP session also ends the clients' sessions (back-channel logout).
            options.ServerSideSessions.UserDisplayNameClaimType = "name";
            options.ServerSideSessions.RemoveExpiredSessions = true;
            options.ServerSideSessions.ExpiredSessionsTriggerBackchannelLogout = true;

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ FAPI 2.0 for private_key_jwt: the client's assertion must name EXACTLY this
            // │ issuer as its audience (not the token endpoint or any list) - an assertion made
            // │ for another server is never accepted here. ES256 / PS256 only.
            // └──────────────────────────────────────────────────────────────────────────────────────
            options.StrictClientAssertionAudienceValidation = true;
            options.SupportedClientAssertionSigningAlgorithms = ["ES256", "PS256"];
        })
        .AddInMemoryIdentityResources(Config.IdentityResources)
        .AddInMemoryApiScopes(Config.ApiScopes)
        .AddInMemoryApiResources(Config.ApiResources)
        .AddInMemoryClients(clients)
        // ┌─ RESIL + SEC ────────────────────────────────────────────────────────────────────────
        // │ Operational store in PostgreSQL: refresh tokens (hashed handles), PAR requests and
        // │ signing keys survive a restart and are shared by IDP instances; hourly clean-up. In
        // │ memory, a restart would invalidate every refresh token.
        // └──────────────────────────────────────────────────────────────────────────────────────
        .AddOperationalStore(options =>
        {
            options.ConfigureDbContext = db => db.UseNpgsql(identityDb);
            options.DefaultSchema = "identity_server";
            options.EnableTokenCleanup = true;
            options.TokenCleanupInterval = 3600;
        })
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ Server-side sign-in sessions: sign-out deletes the row, so a cookie copied before
        // │ sign-out is refused (otherwise it would keep working for up to 10 hours).
        // └──────────────────────────────────────────────────────────────────────────────────────
        .AddServerSideSessions<ServerSideSessionStore>()
        .AddProfileService<CustomProfileService>()
        // OAuth 2.0 Token Exchange (RFC 8693): a service swaps a person's token for one aimed at
        // the next service, keeping the person as subject and naming itself in "act".
        .AddExtensionGrantValidator<TokenExchangeGrantValidator>()
        // private_key_jwt (RFC 7523): clients that sign an assertion with their own key - no secret.
        .AddJwtBearerClientAuthentication()
        .AddSigningCredential(builder);

    // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
    // │ Health endpoints for the orchestrator's probes: /health/live (process working) and
    // │ /health/ready (database reachable). Anonymous; no internals in the body.
    // └──────────────────────────────────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthEndpoints.LiveTag])
        .AddDbContextCheck<IdentityDbContext>("database", tags: [HealthEndpoints.ReadyTag]);

    var app = builder.Build();

    // Application logging and exception handling.
    app.UseEwpRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();

    app.UseRouting();
    // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
    // │ Order matters: routing, then the rate limiter (so the "login" policy applies), then
    // │ IdentityServer and authorization.
    // └──────────────────────────────────────────────────────────────────────────────────────
    app.UseRateLimiter();

    app.UseCookiePolicy();

    // Authentication & Authorization.
    app.UseIdentityServer();
    app.UseAuthorization();

    // Endpoints.
    app.MapControllers();
    app.MapRazorPages();

    app.MapEwpHealthEndpoints();
    app.MapEwpMetricsEndpoint();   // Prometheus scrape (GET /metrics)

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception");
}
finally
{
    Log.Information("Shut down complete");
    Log.CloseAndFlush();
}