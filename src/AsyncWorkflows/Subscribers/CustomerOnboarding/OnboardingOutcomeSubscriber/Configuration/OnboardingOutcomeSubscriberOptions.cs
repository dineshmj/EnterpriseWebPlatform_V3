using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.OnboardingOutcomeSubscriber.Configuration;

public sealed class OnboardingOutcomeSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "OnboardingOutcomeSubscriber";

    /// <summary>Fixed in code: the topics are part of this worker's contract, not configuration.</summary>
    public IReadOnlyList<string> Topics { get; } =
    [
        KafkaTopicNames.KycCaseCreated,
        KafkaTopicNames.KycCaseApproved,
        KafkaTopicNames.KycCaseRejected
    ];

    public string GroupId { get; init; } = "customer-onboarding-kyc-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.OnboardingOutcomeSubscriberDeadLetter;

    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;

    public string IdentityServerAuthority { get; init; } = IDP.AUTHORITY;

    public string ClientId { get; init; } =
        CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP_FOR_ONBOARDING_OUTCOME_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M;

    /// <summary>From configuration / secret store only; no compiled-in default.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE;

    public string CustomerOnboardingApiBaseUrl { get; init; } = CustomerOnboardingMicroservice.MICROSERVICE_API_BASE_URL;
}
