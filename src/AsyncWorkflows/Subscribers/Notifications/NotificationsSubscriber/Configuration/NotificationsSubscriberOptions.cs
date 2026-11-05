using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Notifications.NotificationsSubscriber.Configuration;

public sealed class NotificationsSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "NotificationsSubscriber";

    /// <summary>
    /// Fixed in code: the topics are part of this worker's contract, not configuration.
    /// New work for the next team ("created"; for Compliance "screened", when the case is
    /// ready for a decision; for Payments "approval required") and outcomes for the initiator.
    /// </summary>
    public IReadOnlyList<string> Topics { get; } =
    [
        KafkaTopicNames.KycCaseCreated,
        KafkaTopicNames.KycCaseApproved,
        KafkaTopicNames.KycCaseRejected,
        KafkaTopicNames.ComplianceCaseScreened,
        KafkaTopicNames.ComplianceCaseApproved,
        KafkaTopicNames.ComplianceCaseRejected,
        KafkaTopicNames.AccountApplicationCreated,
        KafkaTopicNames.AccountApplicationRejected,
        KafkaTopicNames.AccountOpened,
        KafkaTopicNames.AccountOpeningFailed,
        KafkaTopicNames.PaymentEvents,
        // An erasure waiting for a second operator (new work), and its outcome for the requester.
        KafkaTopicNames.CustomerPersonalDataErasure
    ];

    /// <summary>The Kafka ACLs grant this worker's user READ on this group only.</summary>
    public string GroupId { get; init; } = "notifications.subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.NotificationsSubscriberDeadLetter;

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

    public string Scope { get; init; } = "notifications.write";

    /// <summary>From configuration only (https); no compiled-in default.</summary>
    public string NotificationsApiBaseUrl { get; init; } = string.Empty;
}