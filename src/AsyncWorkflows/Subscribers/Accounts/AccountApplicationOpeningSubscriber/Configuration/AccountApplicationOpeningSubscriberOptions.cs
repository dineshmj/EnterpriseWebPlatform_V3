using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Accounts.AccountApplicationOpeningSubscriber.Configuration;

public sealed class AccountApplicationOpeningSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "AccountApplicationOpeningSubscriber";

    /// <summary>Fixed in code: the topic is part of this worker's contract, not configuration.</summary>
    public IReadOnlyList<string> Topics { get; } = [KafkaTopicNames.ComplianceCaseApproved];

    /// <summary>The Kafka ACLs grant this worker's user READ on this group only.</summary>
    public string GroupId { get; init; } = "accounts.application-opening-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.AccountApplicationOpeningSubscriberDeadLetter;

    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;

    public string IdentityServerAuthority { get; init; } = IDP.AUTHORITY;

    public string ClientId { get; init; } =
        AccountsMicroservice.CLIENT_ID_FOR_IDP_FOR_ACCOUNT_APPLICATION_OPENING_SUBSCRIBER_TO_ACCOUNTS_API_M2M;

    /// <summary>From configuration / secret store only; no compiled-in default.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = AccountsApiScopesRequired.ACCOUNTS_WRITE;

    public string AccountsApiBaseUrl { get; init; } = AccountsMicroservice.MICROSERVICE_API_BASE_URL;
}