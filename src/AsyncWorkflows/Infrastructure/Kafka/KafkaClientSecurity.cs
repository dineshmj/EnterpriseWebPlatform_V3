using Confluent.Kafka;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

// ┌─ :: SECURITY :: Kafka TLS and authentication, fail closed ─────────────────────────────────────
// │ Why : Without credentials any program on the network could publish a forged business event
// │       (e.g. a fake "application submitted") or read every message; without TLS anyone who can
// │       watch the traffic reads the events, or poses as the broker.
// │ How : SaslSsl: TLS with the broker's certificate checked against SslCaLocation AND its host
// │       name (endpoint identification), then each deployable's OWN SCRAM-SHA-512 user. A SASL
// │       protocol without a user name or password, or a CA file that is not there, stops the
// │       start-up instead of silently connecting unauthenticated or untrusted. The broker's ACLs
// │       do the rest.
// │ Ref : Features 2.4.6 | kafka/README.md | OWASP A02 Cryptographic Failures, A07 Identification
// │       and Authentication Failures | CWE-306, CWE-319, CWE-297
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Applies <see cref="KafkaOptions"/> to a Confluent producer or consumer configuration.</summary>
public static class KafkaClientSecurity
{
    /// <summary>
    /// Sets the bootstrap servers, for SaslSsl the trusted CA and host-name check, and for a
    /// SASL protocol the SCRAM credentials. Fails closed: incomplete settings throw at
    /// start-up instead of silently connecting unauthenticated.
    /// </summary>
    public static T Apply<T>(T config, KafkaOptions options) where T : ClientConfig
    {
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the Kafka settings are incomplete (e.g. SASL without a user name or password),
        // │ stop the start-up - never connect unauthenticated.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (Validate(options) is { } error)
            throw new InvalidOperationException(error);

        config.BootstrapServers = options.BootstrapServers;

        var protocol = Enum.Parse<SecurityProtocol>(options.SecurityProtocol, ignoreCase: true);
        config.SecurityProtocol = protocol;

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If TLS is on: trust only the configured CA (or the OS roots when none is set) and
        // │ check the broker's host name, explicitly - not left to librdkafka's default.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (protocol is SecurityProtocol.Ssl or SecurityProtocol.SaslSsl)
        {
            if (CaLocationOf(options) is { } caLocation)
                config.SslCaLocation = caLocation;
            config.SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.Https;
        }

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
            return $"Kafka:SecurityProtocol '{options.SecurityProtocol}' is not valid (SaslSsl; SaslPlaintext or Plaintext only for a reverted broker).";

        if ((protocol is SecurityProtocol.Ssl or SecurityProtocol.SaslSsl) &&
            CaLocationOf(options) is { } caLocation && !File.Exists(caLocation))
            return $"Kafka:SslCaLocation '{caLocation}' does not exist (local development: run ps\\pki\\New-EwpLocalPki.ps1).";

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

    /// <summary>The CA file with environment variables expanded, or null for the OS's trusted roots.</summary>
    private static string? CaLocationOf(KafkaOptions options) =>
        string.IsNullOrWhiteSpace(options.SslCaLocation)
            ? null
            : Environment.ExpandEnvironmentVariables(options.SslCaLocation);
}