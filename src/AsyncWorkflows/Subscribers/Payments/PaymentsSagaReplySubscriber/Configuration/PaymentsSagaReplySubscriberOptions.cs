using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Payments.PaymentsSagaReplySubscriber.Configuration;

public sealed class PaymentsSagaReplySubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "PaymentsSagaReplySubscriber";

    /// <summary>Fixed in code: the topic is part of this worker's contract, not configuration.</summary>
    public IReadOnlyList<string> Topics { get; } = [KafkaTopicNames.AccountsFundsReplies];

    /// <summary>The Kafka ACLs grant this worker's user READ on this group only.</summary>
    public string GroupId { get; init; } = "payments.saga-reply-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.PaymentsSagaReplySubscriberDeadLetter;

    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;

    public string IdentityServerAuthority { get; init; } = IDP.AUTHORITY;

    public string ClientId { get; init; } =
        PaymentsMicroservice.CLIENT_ID_FOR_IDP_FOR_PAYMENTS_SAGA_REPLY_SUBSCRIBER_TO_PAYMENTS_API_M2M;

    /// <summary>From configuration / secret store only; no compiled-in default.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = PaymentsApiScopesRequired.PAYMENTS_WRITE;

    public string PaymentsApiBaseUrl { get; init; } = PaymentsMicroservice.MICROSERVICE_API_BASE_URL;
}