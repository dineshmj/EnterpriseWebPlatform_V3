using Confluent.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

/// <summary>Applies <see cref="KafkaOptions"/> to a Confluent producer or consumer configuration.</summary>
public static class KafkaClientSecurity
{
    /// <summary>
    /// Sets the bootstrap servers and, for a SASL protocol, the SCRAM credentials.
    /// Fails closed: a SASL protocol without a username and password throws at
    /// start-up instead of silently connecting unauthenticated.
    /// </summary>
    public static T Apply<T>(T config, KafkaOptions options) where T : ClientConfig
    {
        if (Validate(options) is { } error)
            throw new InvalidOperationException(error);

        config.BootstrapServers = options.BootstrapServers;

        var protocol = Enum.Parse<SecurityProtocol>(options.SecurityProtocol, ignoreCase: true);
        config.SecurityProtocol = protocol;

        if (protocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            config.SaslMechanism = Enum.Parse<SaslMechanism>(options.SaslMechanism, ignoreCase: true);
            config.SaslUsername = options.SaslUsername;
            config.SaslPassword = options.SaslPassword;
        }

        return config;
    }

    /// <summary>The configuration problem, or null when the options are usable.</summary>
    public static string? Validate(KafkaOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapServers))
            return "Kafka:BootstrapServers is not configured.";

        if (!Enum.TryParse<SecurityProtocol>(options.SecurityProtocol, ignoreCase: true, out var protocol))
            return $"Kafka:SecurityProtocol '{options.SecurityProtocol}' is not valid (Plaintext, SaslPlaintext, SaslSsl).";

        if (protocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            if (!Enum.TryParse<SaslMechanism>(options.SaslMechanism, ignoreCase: true, out _))
                return $"Kafka:SaslMechanism '{options.SaslMechanism}' is not valid.";
            if (string.IsNullOrWhiteSpace(options.SaslUsername))
                return $"Kafka:SecurityProtocol is {protocol}, but Kafka:SaslUsername is not configured.";
            if (string.IsNullOrWhiteSpace(options.SaslPassword))
                return $"Kafka:SecurityProtocol is {protocol}, but Kafka:SaslPassword is not configured " +
                       "(Development: appsettings.Development.json with DOTNET_ENVIRONMENT=Development; otherwise the Kafka__SaslPassword environment variable or a secret store).";
        }

        return null;
    }
}
