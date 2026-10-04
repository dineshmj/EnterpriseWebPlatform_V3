using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public static class KafkaServiceCollectionExtensions
{
    /// <summary>
    /// Binds the "Kafka" section and validates it when the host STARTS: a component
    /// configured for SASL without its credentials refuses to start with a clear
    /// message, instead of running and failing quietly on every publish or poll.
    /// </summary>
    public static OptionsBuilder<KafkaOptions> AddKafkaOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<KafkaOptions>, KafkaOptionsValidator>());

        return services
            .AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .ValidateOnStart();
    }

    private sealed class KafkaOptionsValidator : IValidateOptions<KafkaOptions>
    {
        public ValidateOptionsResult Validate(string? name, KafkaOptions options) =>
            KafkaClientSecurity.Validate(options) is { } error
                ? ValidateOptionsResult.Fail(error)
                : ValidateOptionsResult.Success;
    }
}
