using System.Threading.RateLimiting;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Duende.IdentityServer.EntityFramework.Stores;
using Npgsql;
using OpenTelemetry.Trace;
using Serilog;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Common.WebUtilities.Security;
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

    // Distributed tracing: W3C trace context across HTTP and Kafka; spans exported
    // over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set (see ReadMe.txt); Serilog logs and
    // Prometheus metrics come with it.
    builder.AddEwpObservability("identity-server", tracing => tracing.AddAspNetCoreInstrumentation().AddNpgsql());

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

    builder.Services.AddRazorPages();

    // Login throttling per client IP, complementing per-account lockout
    // (UserRepository): slows password spraying across many usernames.
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

    // Client secrets come from this deployable's configuration. The client list
    // is materialized now, so a missing secret stops start-up (fail closed).
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The client list is built NOW, so a missing client secret stops the start-up instead
    // │ of failing at the first sign-in.
    // └──────────────────────────────────────────────────────────────────────────────────────
    ClientSecretStore.Initialize(builder.Configuration);
    // Client IDs and the web clients' addresses (redirect URIs) - configuration, not code.
    ClientSettings.Initialize(builder.Configuration);
    var clients = Config.GetClients(builder.Environment.IsDevelopment()).ToList();

    builder.Services
        .AddIdentityServer(options =>
        {
            options.Events.RaiseErrorEvents = true;
            options.Events.RaiseInformationEvents = true;
            options.Events.RaiseFailureEvents = true;
            options.Events.RaiseSuccessEvents = true;

            // Server-side sessions (below): the session's display name for administration, and an
            // expired IDP session also ends the clients' sessions (back-channel logout).
            options.ServerSideSessions.UserDisplayNameClaimType = "name";
            options.ServerSideSessions.RemoveExpiredSessions = true;
            options.ServerSideSessions.ExpiredSessionsTriggerBackchannelLogout = true;
        })
        .AddInMemoryIdentityResources(Config.IdentityResources)
        .AddInMemoryApiScopes(Config.ApiScopes)
        .AddInMemoryApiResources(Config.ApiResources)
        .AddInMemoryClients(clients)
        // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
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
        .AddSigningCredential(builder);

    // Health endpoints for the orchestrator's probes: /health/live (process working)
    // and /health/ready (dependencies reachable). Anonymous; no internals in the body.
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