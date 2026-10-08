using System.Threading.RateLimiting;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
    builder.Services.Configure<MfaOptions>(builder.Configuration.GetSection(MfaOptions.SectionName));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<MfaService>();

    builder.Services.AddRazorPages();

    // Login throttling per client IP, complementing per-account lockout
    // (UserRepository): slows password spraying across many usernames.
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

    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.SameSite = SameSiteMode.None;
        // WHY:
        // Required for the current cross-site OIDC/OAuth and iframe-based
        // authentication flows when the cookie is used across origins.
        //
        // IF NOT:
        // Modern browsers may block the cookie during cross-site callbacks,
        // potentially causing authentication or silent-login failures.
    });

    // Client secrets come from this deployable's configuration. The client list
    // is materialized now, so a missing secret stops start-up (fail closed).
    ClientSecretStore.Initialize(builder.Configuration);
    var clients = Config.GetClients(builder.Environment.IsDevelopment()).ToList();

    builder.Services
        .AddIdentityServer(options =>
        {
            options.Events.RaiseErrorEvents = true;
            options.Events.RaiseInformationEvents = true;
            options.Events.RaiseFailureEvents = true;
            options.Events.RaiseSuccessEvents = true;
        })
        .AddInMemoryIdentityResources(Config.IdentityResources)
        .AddInMemoryApiScopes(Config.ApiScopes)
        .AddInMemoryApiResources(Config.ApiResources)
        .AddInMemoryClients(clients)
        .AddOperationalStore(options =>
        {
            options.ConfigureDbContext = db => db.UseNpgsql(identityDb);
            options.DefaultSchema = "identity_server";
            options.EnableTokenCleanup = true;
            options.TokenCleanupInterval = 3600;
        })
        // 🡡__ WHY   : Refresh tokens, pushed authorization requests and Duende's signing keys are stored in
        //              PostgreSQL (refresh tokens by a hash of the handle only; their details encrypted), so an IDP
        //              restart no longer forgets them and several IDP instances share them. Expired rows are
        //              removed hourly.
        // 🡡__ IF NOT: The in-memory default: after a restart no refresh token works, and everybody must sign in
        //              again once their access token expires.
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