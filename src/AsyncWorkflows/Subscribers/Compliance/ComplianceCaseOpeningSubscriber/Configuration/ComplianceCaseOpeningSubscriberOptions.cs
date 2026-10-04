using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.Compliance.ComplianceCaseOpeningSubscriber.Configuration;

public sealed class ComplianceCaseOpeningSubscriberOptions : ISubscriberSettings, IM2MClientSettings
{
    public const string SectionName = "ComplianceCaseOpeningSubscriber";

    /// <summary>Fixed in code: the topic is part of this worker's contract, not configuration.</summary>
    public IReadOnlyList<string> Topics { get; } = [KafkaTopicNames.KycCaseApproved];

    /// <summary>The Kafka ACLs grant this worker's user READ on this group only.</summary>
    public string GroupId { get; init; } = "compliance.case-opening-subscriber";

    public string DeadLetterTopic { get; init; } = KafkaTopicNames.ComplianceCaseOpeningSubscriberDeadLetter;

    public int TransientRetryInitialDelaySeconds { get; init; } = 2;

    public int TransientRetryMaxDelaySeconds { get; init; } = 60;

    public string IdentityServerAuthority { get; init; } = IDP.AUTHORITY;

    public string ClientId { get; init; } =
        ComplianceMicroservice.CLIENT_ID_FOR_IDP_FOR_COMPLIANCE_CASE_OPENING_SUBSCRIBER_TO_COMPLIANCE_API_M2M;

    /// <summary>From configuration / secret store only; no compiled-in default.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = ComplianceApiScopesRequired.COMPLIANCE_WRITE;

    public string ComplianceApiBaseUrl { get; init; } = ComplianceMicroservice.MICROSERVICE_API_BASE_URL;
}