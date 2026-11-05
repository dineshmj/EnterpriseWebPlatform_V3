using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Compliance.ComplianceCaseOpeningSubscriber.Configuration;

public sealed class ComplianceCaseOpeningSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "ComplianceCaseOpeningSubscriber";

    /// <summary>Fixed in code: the topic is part of this worker's contract, not configuration.</summary>
    public IReadOnlyList<string> Topics { get; } = [KafkaTopicNames.KycCaseApproved, KafkaTopicNames.CustomerPersonalDataErasure];

    /// <summary>The Kafka ACLs grant this worker's user READ on this group only.</summary>
    public string GroupId { get; init; } = "compliance.case-opening-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.ComplianceCaseOpeningSubscriberDeadLetter;

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

    public string Scope { get; init; } = "compliance.write";

    /// <summary>From configuration only (https); no compiled-in default.</summary>
    public string ComplianceApiBaseUrl { get; init; } = string.Empty;
}