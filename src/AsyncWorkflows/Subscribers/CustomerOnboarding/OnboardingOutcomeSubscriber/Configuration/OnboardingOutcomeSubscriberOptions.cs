using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.OnboardingOutcomeSubscriber.Configuration;

public sealed class OnboardingOutcomeSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "OnboardingOutcomeSubscriber";

    /// <summary>Fixed in code: the topics are part of this worker's contract, not configuration.</summary>
    public IReadOnlyList<string> Topics { get; } =
    [
        KafkaTopicNames.KycCaseCreated,
        KafkaTopicNames.KycCaseApproved,
        KafkaTopicNames.KycCaseRejected,
        KafkaTopicNames.ComplianceCaseCreated,
        KafkaTopicNames.ComplianceCaseApproved,
        KafkaTopicNames.ComplianceCaseRejected,
        KafkaTopicNames.AccountApplicationCreated,
        KafkaTopicNames.AccountOpened,
        KafkaTopicNames.AccountApplicationRejected,
        KafkaTopicNames.AccountOpeningFailed
    ];

    public string GroupId { get; init; } = "customer-onboarding.outcome-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.OnboardingOutcomeSubscriberDeadLetter;

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

    public string Scope { get; init; } = "customer-onboarding.write";

    /// <summary>From configuration only (https); no compiled-in default.</summary>
    public string CustomerOnboardingApiBaseUrl { get; init; } = string.Empty;
}