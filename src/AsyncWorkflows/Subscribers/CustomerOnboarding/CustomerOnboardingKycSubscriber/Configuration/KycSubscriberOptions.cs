using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Configuration;

public sealed class KycSubscriberOptions
{
    public const string SectionName = "CustomerOnboardingKycSubscriber";

    public string[] Topics { get; init; } =
    [
        KafkaTopicNames.KycCaseCreated,
        KafkaTopicNames.KycCaseApproved,
        KafkaTopicNames.KycCaseRejected
    ];

    /// <summary>
    /// All instances share this consumer group: Kafka assigns each partition to
    /// exactly one instance, so two instances never work on the same message.
    /// </summary>
    public string GroupId { get; init; } = "customer-onboarding-kyc-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.CustomerOnboardingKycSubscriberDeadLetter;

    public string IdentityServerAuthority { get; init; } = IDP.AUTHORITY;

    public string ClientId { get; init; } =
        CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP_FOR_CUST_ONBOARDING_KYC_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M;

    /// <summary>From configuration / secret store only; no compiled-in default.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE;

    public string CustomerOnboardingApiBaseUrl { get; init; } = CustomerOnboardingMicroservice.MICROSERVICE_API_BASE_URL;

    /// <summary>Back-off while a TRANSIENT failure persists (the message is retried in place, never skipped).</summary>
    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;
}
