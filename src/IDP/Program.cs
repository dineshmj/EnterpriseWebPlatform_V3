using System.Threading.RateLimiting;

using Microsoft.EntityFrameworkCore;

using Serilog;

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

    builder.Host.UseSerilog((ctx, lc) => lc
        .WriteTo.Console(
            outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}{NewLine}")
        .Enrich.FromLogContext()
        .ReadFrom.Configuration(ctx.Configuration));

    // PostgreSQL database for identity and authorization data.
    builder.Services.AddDbContext<IdentityDbContext>(options =>
        options.UseNpgsql(
            builder.Configuration.GetConnectionString("IdentityDbConnection")));

    builder.Services.AddScoped<IUserRepository, UserRepository>();
    builder.Services.AddScoped<IPasswordManager, PasswordManager>();

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
        .AddProfileService<CustomProfileService>()
        .AddSigningCredential(builder);

    var app = builder.Build();

    // Application logging and exception handling.
    app.UseSerilogRequestLogging();

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