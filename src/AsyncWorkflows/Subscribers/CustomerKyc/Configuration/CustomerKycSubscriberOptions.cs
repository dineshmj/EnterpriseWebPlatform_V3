using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;

public sealed class CustomerKycSubscriberOptions
{
    public const string SectionName = "CustomerKycSubscriber";

    public string Topic { get; init; } = "customer.created";
    public string GroupId { get; init; } = "customer-kyc-subscriber";

    public string IdentityServerAuthority { get; init; } = IDP.AUTHORITY;
    public string ClientId { get; init; } = CustomerKycMicroservice.CLIENT_ID_FOR_IDP_FOR_CUST_KYC_SUBSCRIBER_TO_CUST_KYC_API_M2M;
    public string ClientSecret { get; init; } = CustomerKycMicroservice.CLIENT_SECRET_FOR_IDP_FOR_CUST_KYC_SUBSCRIBER_TO_CUST_KYC_API_M2M;
    public string Scope { get; init; } = CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE;
    public string KycApiBaseUrl { get; init; } = CustomerKycMicroservice.MICROSERVICE_API_BASE_URL;

    public int MaxAttempts { get; init; } = 3;
    public int InitialRetryDelayMilliseconds { get; init; } = 500;
    public int RequestTimeoutSeconds { get; init; } = 15;
}
