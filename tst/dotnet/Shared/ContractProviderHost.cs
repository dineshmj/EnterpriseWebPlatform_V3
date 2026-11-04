using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseWebPlatform.Tests.Contracts;

// ┌─ :: ARCHITECTURE + SECURITY :: The real API, hosted for the Pact verifier ─────────────────────
// │ Why : A provider contract test must exercise what callers really meet - routing, model
// │       binding, the controllers, problem details, the status codes - not a hand-written copy.
// │ How : The API's own Program (WebApplicationFactory) on Kestrel at a free localhost port, so the
// │       Pact verifier (native code) can call it over HTTP. Only the edges change:
// │        - persistence: the application's ports are mocks, set per PROVIDER STATE through
// │          POST /provider-states (what the verifier calls before each interaction);
// │        - identity: a test scheme signs in the CONSUMER's machine client (client_id + scope),
// │          so the real authorization policies still run. Token validation and DPoP are not
// │          what a contract is about - they are proven by the security tests;
// │        - the API's background loops (Outbox relay, workers) are not started.
// │ Ref : tst/contracts/README.md | Pact provider states | ASP.NET Core WebApplicationFactory
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The edges a provider contract test replaces, and starting the host on a real port.</summary>
public static class ContractProviderHost
{
    public const string ProviderStatesPath = "/provider-states";

    private const string SchemeName = "ContractTest";

    /// <summary>
    /// Configures the host: <paramref name="callerOf"/> names the machine client (and its scope) calling a path;
    /// <paramref name="setState"/> receives each provider state before its interaction.
    /// </summary>
    public static void ForContracts(
        this IWebHostBuilder builder, Assembly api, Func<PathString, (string ClientId, string Scope)> callerOf, Action<string?> setState)
    {
        builder.UseEnvironment(Environments.Development);
        // NOTE: no Kafka here - the CA file of a developer's machine is not needed to start.
        builder.UseSetting("Kafka:SslCaLocation", string.Empty);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureTestServices(services =>
        {
            foreach (var loop in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Assembly == api).ToList())
                services.Remove(loop);

            services.AddSingleton(new Caller(callerOf));
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, MachineCallerHandler>(SchemeName, _ => { });
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = SchemeName;
                options.DefaultAuthenticateScheme = SchemeName;
                options.DefaultChallengeScheme = SchemeName;
                options.DefaultForbidScheme = SchemeName;
            });

            services.AddSingleton<IStartupFilter>(new ProviderStateEndpoint(setState));
        });
    }

    /// <summary>Starts the API on Kestrel and returns its address as http://localhost:{port} (the APIs serve "localhost").</summary>
    public static Uri StartOnLocalhost<TProgram>(this WebApplicationFactory<TProgram> factory) where TProgram : class
    {
        // A free port (0) on loopback only: two providers' tests may run at the same time.
        factory.UseKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, 0));
        factory.StartServer();
        var address = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return new UriBuilder(address) { Host = "localhost" }.Uri;
    }

    private sealed record Caller(Func<PathString, (string ClientId, string Scope)> Of);

    private sealed class MachineCallerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Caller caller)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var (clientId, scope) = caller.Of(Request.Path);
            var identity = new ClaimsIdentity(
                [new Claim("sub", clientId), new Claim("client_id", clientId), new Claim("scope", scope)], SchemeName, "sub", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class ProviderStateEndpoint(Action<string?> setState) : IStartupFilter
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Before everything else: the verifier's set-up call is not an API call.
            app.Map(ProviderStatesPath, setup => setup.Run(async context =>
            {
                var request = await JsonSerializer.DeserializeAsync<ProviderStateRequest>(context.Request.Body, Json, context.RequestAborted);
                if (request?.Action != "teardown")
                    setState(request?.State);
                context.Response.StatusCode = StatusCodes.Status200OK;
            }));
            next(app);
        };
    }

    private sealed record ProviderStateRequest(string? State, string? Action);
}