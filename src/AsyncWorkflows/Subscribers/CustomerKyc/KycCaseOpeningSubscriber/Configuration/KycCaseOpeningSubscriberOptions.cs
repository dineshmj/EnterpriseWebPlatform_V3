using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.KycCaseOpeningSubscriber.Configuration;

public sealed class KycCaseOpeningSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "KycCaseOpeningSubscriber";

    /// <summary>Fixed in code: the topic is part of this worker's contract, not configuration.</summary>
    public IReadOnlyList<string> Topics { get; } = [KafkaTopicNames.OnboardingApplicationSubmitted];

    /// <summary>
    /// Kept from before the rename, so committed offsets stay valid; revisited with
    /// the per-topic Kafka ACLs.
    /// </summary>
    public string GroupId { get; init; } = "customer-kyc-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.KycCaseOpeningSubscriberDeadLetter;

    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;

    public string IdentityServerAuthority { get; init; } = IDP.AUTHORITY;

    public string ClientId { get; init; } = CustomerKycMicroservice.CLIENT_ID_FOR_IDP_FOR_KYC_CASE_OPENING_SUBSCRIBER_TO_CUST_KYC_API_M2M;

    /// <summary>
    /// M2M client secret. From this worker's configuration / secret store
    /// (KycCaseOpeningSubscriber__ClientSecret); there is no compiled-in default.
    /// </summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE;

    public string KycApiBaseUrl { get; init; } = CustomerKycMicroservice.MICROSERVICE_API_BASE_URL;
}
