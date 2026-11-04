using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.DocumentsManagement.DocumentInvalidationSubscriber.Configuration;

public sealed class DocumentInvalidationSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "DocumentInvalidationSubscriber";

    /// <summary>
    /// Fixed in code: the topics are part of this worker's contract, not configuration.
    /// Submitted attaches the evidence (retained); rejected invalidates it (compensation).
    /// </summary>
    public IReadOnlyList<string> Topics { get; } =
        [KafkaTopicNames.OnboardingApplicationSubmitted, KafkaTopicNames.OnboardingApplicationRejected];

    /// <summary>The Kafka ACLs grant this worker's user READ on this group only.</summary>
    public string GroupId { get; init; } = "documents-management.invalidation-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.DocumentInvalidationSubscriberDeadLetter;

    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;

    /// <summary>From configuration only (https); no compiled-in default.</summary>
    public string IdentityServerAuthority { get; init; } = string.Empty;

    /// <summary>From configuration only: the client ID the IDP registered for this worker.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// The private_key_jwt key (encrypted PEM and its password file): from configuration only,
    /// mounted from a secret store outside Development; no compiled-in default.
    /// </summary>
    public KeyFileSettings ClientKey { get; init; } = new();

    /// <summary>The key every token of this worker is bound to (DPoP) - not the client key.</summary>
    public KeyFileSettings DPoPKey { get; init; } = new();

    public string Scope { get; init; } = "documents-management.write";

    /// <summary>From configuration only (https); no compiled-in default.</summary>
    public string DocumentsManagementApiBaseUrl { get; init; } = string.Empty;
}