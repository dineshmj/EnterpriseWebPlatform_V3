namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

/// <summary>
/// Connection settings for one Kafka client (producer or consumer). Each deployable
/// authenticates as its OWN Kafka user, so the broker's ACLs can limit it to its own
/// topics and consumer group (see ps/ps/kafka/Setup-KafkaSecurity.ps1).
/// </summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = string.Empty;

    /// <summary>
    /// "Plaintext" (no authentication) or "SaslPlaintext" / "SaslSsl" (SCRAM). Local
    /// development uses SaslPlaintext on localhost; production uses SaslSsl.
    /// </summary>
    public string SecurityProtocol { get; init; } = "Plaintext";

    public string SaslMechanism { get; init; } = "ScramSha512";

    public string? SaslUsername { get; init; }

    /// <summary>From configuration / secret store only; never compiled in.</summary>
    public string? SaslPassword { get; init; }
}