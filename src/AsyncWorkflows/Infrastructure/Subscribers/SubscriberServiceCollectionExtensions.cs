using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

// ┌─ :: ARCHITECTURE + SECURITY :: Fail-closed worker configuration ───────────────────────────────
// │ Why : A worker that starts without its topics, dead-letter topic or client secret would run
// │       "half working" and fail silently later - or worse, call an API unauthenticated.
// │ How : Settings are validated at start-up (ValidateOnStart); a missing client secret, client ID,
// │       IDP or API address (https only), topic or dead-letter topic stops the worker at once.
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
    /// machine identity, and the "IdentityServer" HttpClient it uses. The worker
    /// refuses to start without a client secret (fail closed).
    /// </summary>
    public static IHttpClientBuilder AddCachedM2MTokenClient<TSettings>(
        this IServiceCollection services,
        string sectionName)
        where TSettings : class, IM2MClientSettings
    {
        services
            .AddOptions<TSettings>()
            .Validate(s => !string.IsNullOrWhiteSpace(s.ClientSecret), $"{sectionName}:ClientSecret is not configured.")
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ The IDP's address and the worker's client ID come from configuration only (no
            // │ compiled-in default): missing, or an IDP address that is not https = no start-up.
            // └──────────────────────────────────────────────────────────────────────────────────────
            .Validate(s => !string.IsNullOrWhiteSpace(s.ClientId), $"{sectionName}:ClientId is not configured.")
            .Validate(s => ServiceAddress.IsHttps(s.IdentityServerAuthority), $"{sectionName}:IdentityServerAuthority must be an absolute https address.")
            .ValidateOnStart();

        services.AddSingleton(sp => new CachedM2MTokenClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<TSettings>>().Value,
            sp.GetRequiredService<ILogger<CachedM2MTokenClient>>()));

        return services.AddHttpClient(CachedM2MTokenClient.HttpClientName, (sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<TSettings>>().Value;
            client.BaseAddress = new Uri(settings.IdentityServerAuthority.TrimEnd('/'));
        });
    }
}