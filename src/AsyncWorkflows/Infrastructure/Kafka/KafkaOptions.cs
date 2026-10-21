namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

// ┌─ :: SECURITY :: One Kafka identity per deployable ─────────────────────────────────────────────
// │ Why : A shared Kafka account would let any component act as any other one.
// │ How : Each deployable has its own user name in its appsettings.json and its own password from
// │       Development settings or a secret store - never compiled in. Every environment, localhost
// │       included, uses SaslSsl (TLS, broker certificate checked against SslCaLocation).
// │ Ref : Features 2.4.5, 2.4.6 | Threat-Model R-10 |
// │       OWASP A02 Cryptographic Failures | CWE-319
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Connection settings for one Kafka client (producer or consumer). Each deployable
/// authenticates as its OWN Kafka user, so the broker's ACLs can limit it to its own
/// topics and consumer group (see ps/kafka/Setup-KafkaSecurity.ps1).
/// </summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = string.Empty;

    /// <summary>
    /// "SaslSsl" (TLS + SCRAM) everywhere, local development included. "SaslPlaintext"
    /// (SCRAM without TLS) and "Plaintext" (neither) remain only for a broker reverted with
    /// Setup-KafkaSecurity.ps1 -Revert. The default is the safe one.
    /// </summary>
    public string SecurityProtocol { get; init; } = "SaslSsl";

    /// <summary>
    /// PEM file with the certificate authority that issued the broker's certificate
    /// (local development: %USERPROFILE%\.ewp-pki\ewp-trust-root.pem; environment variables
    /// are expanded). Empty = the operating system's trusted roots (e.g. a managed Kafka
    /// service with a public certificate).
    /// </summary>
    public string? SslCaLocation { get; init; }

    public string SaslMechanism { get; init; } = "ScramSha512";

    public string? SaslUsername { get; init; }

    /// <summary>From configuration / secret store only; never compiled in.</summary>
    public string? SaslPassword { get; init; }
}