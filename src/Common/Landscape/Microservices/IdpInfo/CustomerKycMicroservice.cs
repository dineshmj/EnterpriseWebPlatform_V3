namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

public static class CustomerKycMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Customer KYC Microservice BFF Client";
	public const string CLIENT_ID_FOR_IDP = "CustomerKYC.Microservice.BFF.ClientID";
	public const string CLIENT_SECRET_FOR_IDP = "3ac91ab3-7ba0-4727-b4f7-36120bec10c5";     // Random GUID for demo purposes only.


    // Customer KYC Subscriber to Customer KYC API M2M
    public const string CLIENT_NAME_FOR_IDP_FOR_CUST_KYC_SUBSCRIBER_TO_CUST_KYC_API_M2M = "Customer KYC Subscriber to Customer KYC API M2M Client";
    public const string CLIENT_ID_FOR_IDP_FOR_CUST_KYC_SUBSCRIBER_TO_CUST_KYC_API_M2M = "CustomerKyc.Subscriber.To.CustomerKycApi.M2M.ClientID";
    public const string CLIENT_SECRET_FOR_IDP_FOR_CUST_KYC_SUBSCRIBER_TO_CUST_KYC_API_M2M = "7a6c5096-12fd-4c24-a039-29e5c93cd753";     // Random GUID for demo purposes only.


    public const string BFF_CLIENT_BASE_URL = "https://kyc.dev.localhost:33800";
	public const string MICROSERVICE_API_BASE_URL = "https://kyc-api.dev.localhost:44305";
}