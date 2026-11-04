using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

// ┌─ :: ARCHITECTURE + SECURITY :: Fail-closed worker configuration ───────────────────────────────
// │ Why : A worker that starts without its topics, dead-letter topic or client key would run
// │       "half working" and fail silently later - or worse, call an API unauthenticated.
// │ How : Settings are validated at start-up (ValidateOnStart); a missing or unreadable client or
// │       DPoP key, client ID, IDP or API address (https only), topic or dead-letter topic stops
// │       the worker at once.
// │ Ref : Features 1.6.5 | OWASP A05 Security Misconfiguration | ASVS V14.1
// └────────────────────────────────────────────────────────────────────────────────────────────────
public static class SubscriberServiceCollectionExtensions
{
    /// <summary>
    /// Registers a reliable Kafka subscriber: the worker's settings (bound from
    /// <paramref name="sectionName"/>, validated at start-up), the shared idempotent
    /// producer for dead-lettering, the processor and the shared consume loop.
    /// </summary>
    public static OptionsBuilder<TSettings> AddKafkaSubscriber<TProcessor, TSettings>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TProcessor : class, IMessageProcessor
        where TSettings : class, ISubscriberSettings
    {
        services.AddKafkaOptions(configuration);   // validated at start-up: no credentials, no start
        services.AddSingleton<IKafkaProducer, KafkaProducer>();
        services.AddSingleton<TProcessor>();
        services.AddSingleton<SubscriberHealth>();
        services.AddHostedService<KafkaSubscriberHostedService<TProcessor, TSettings>>();

        // Liveness: the consume loop is running. Readiness: it has joined its consumer
        // group (Degraded while a message is retried in place). Served over HTTP by
        // AddWorkerHealthEndpoints (Common.Observability).
        services.AddHealthChecks()
            .AddCheck<SubscriberLivenessCheck>("consume-loop", tags: ["live"])
            .AddCheck<SubscriberReadinessCheck>("consumer-group", tags: ["ready"]);

        return services
            .AddOptions<TSettings>()
            .Bind(configuration.GetSection(sectionName))
            .Validate(s => s.Topics.Count > 0, $"{sectionName}: no topics configured.")
            .Validate(s => !string.IsNullOrWhiteSpace(s.GroupId), $"{sectionName}:GroupId is not configured.")
            .Validate(s => !string.IsNullOrWhiteSpace(s.DeadLetterTopic), $"{sectionName}:DeadLetterTopic is not configured.")
            .ValidateOnStart();
    }

    /// <summary>
    /// Registers the cached Client Credentials token client for the worker's own
    /// machine identity, its two keys, and the "IdentityServer" HttpClient it uses. The
    /// worker refuses to start without readable keys (fail closed).
    /// </summary>
    public static IHttpClientBuilder AddCachedM2MTokenClient<TSettings>(
        this IServiceCollection services,
        string sectionName)
        where TSettings : class, IM2MClientSettings
    {
        services
            .AddOptions<TSettings>()
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ The IDP's address, the worker's client ID and its two key files come from
            // │ configuration only (no compiled-in default): missing, an IDP address that is not
            // │ https, or a key that cannot be read = no start-up.
            // └──────────────────────────────────────────────────────────────────────────────────────
            .Validate(s => !string.IsNullOrWhiteSpace(s.ClientId), $"{sectionName}:ClientId is not configured.")
            .Validate(s => ServiceAddress.IsHttps(s.IdentityServerAuthority), $"{sectionName}:IdentityServerAuthority must be an absolute https address.")
            .Validate(s => IsSet(s.ClientKey), $"{sectionName}:ClientKey:Path and :PasswordPath are not configured.")
            .Validate(s => IsSet(s.DPoPKey), $"{sectionName}:DPoPKey:Path and :PasswordPath are not configured.")
            .Validate(s =>
            {
                using var keys = MachineKeys.Load(s, TimeProvider.System);   // throws with the reason
                return true;
            }, $"{sectionName}: the client key or DPoP key cannot be read.")
            .ValidateOnStart();

        services.AddSingleton(sp => MachineKeys.Load(sp.GetRequiredService<IOptions<TSettings>>().Value, TimeProvider.System));
        services.AddSingleton(sp => new CachedM2MTokenClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<TSettings>>().Value,
            sp.GetRequiredService<MachineKeys>(),
            sp.GetRequiredService<ILogger<CachedM2MTokenClient>>()));

        var identityServer = services.AddHttpClient(CachedM2MTokenClient.HttpClientName, (sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<TSettings>>().Value;
            client.BaseAddress = new Uri(settings.IdentityServerAuthority.TrimEnd('/'));
        });

        // ┌─ RESIL + SEC ────────────────────────────────────────────────────────────────────────
        // │ Time-outs and a circuit breaker, but NO automatic retry: a resent token request
        // │ would carry the same one-time assertion and proof, which the IDP refuses. A failed
        // │ request leaves the message unprocessed, and the next try signs new ones.
        // └──────────────────────────────────────────────────────────────────────────────────────
        identityServer.AddStandardResilienceHandler(options => options.Retry.ShouldHandle = _ => ValueTask.FromResult(false));
        return identityServer;
    }

    /// <summary>
    /// Puts the worker's DPoP-bound machine token, with a new proof, on every request of this
    /// client. Add it AFTER the client's resilience handler, so each retry signs a proof of its own.
    /// </summary>
    public static IHttpClientBuilder AddDPoPMachineToken(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler(sp => new DPoPMachineTokenHandler(
            sp.GetRequiredService<CachedM2MTokenClient>(),
            sp.GetRequiredService<MachineKeys>()));

    private static bool IsSet(KeyFileSettings? file) =>
        file is not null && !string.IsNullOrWhiteSpace(file.Path) && !string.IsNullOrWhiteSpace(file.PasswordPath);
}