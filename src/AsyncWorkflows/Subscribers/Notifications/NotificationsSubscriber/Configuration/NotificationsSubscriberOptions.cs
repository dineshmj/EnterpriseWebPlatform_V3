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
        KafkaTopicNames.PaymentEvents
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

    /// <summary>From configuration / secret store only; no compiled-in default.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = "notifications.write";

    /// <summary>From configuration only (https); no compiled-in default.</summary>
    public string NotificationsApiBaseUrl { get; init; } = string.Empty;
}