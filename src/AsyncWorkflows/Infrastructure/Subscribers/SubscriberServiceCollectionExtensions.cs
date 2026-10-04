using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

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
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        services.AddSingleton<IKafkaProducer, KafkaProducer>();
        services.AddSingleton<TProcessor>();
        services.AddHostedService<KafkaSubscriberHostedService<TProcessor, TSettings>>();

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
