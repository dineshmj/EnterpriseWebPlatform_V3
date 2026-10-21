using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Accounts.AccountsCommandSubscriber.Configuration;

public sealed class AccountsCommandSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "AccountsCommandSubscriber";

    /// <summary>Fixed in code: the topic is part of this worker's contract, not configuration.</summary>
    public IReadOnlyList<string> Topics { get; } = [KafkaTopicNames.AccountsCommands];

    /// <summary>The Kafka ACLs grant this worker's user READ on this group only.</summary>
    public string GroupId { get; init; } = "accounts.command-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.AccountsCommandSubscriberDeadLetter;

    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;

    /// <summary>From configuration only (https); no compiled-in default.</summary>
    public string IdentityServerAuthority { get; init; } = string.Empty;

    /// <summary>From configuration only: the client ID the IDP registered for this worker.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>From configuration / secret store only; no compiled-in default.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = "accounts.write";

    /// <summary>From configuration only (https); no compiled-in default.</summary>
    public string AccountsApiBaseUrl { get; init; } = string.Empty;
}