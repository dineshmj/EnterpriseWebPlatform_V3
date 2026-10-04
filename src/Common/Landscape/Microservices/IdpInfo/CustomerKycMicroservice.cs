namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

public static class CustomerKycMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Customer KYC Microservice BFF Client";

	public const string CLIENT_ID_FOR_IDP = "CustomerKYC.Microservice.BFF.ClientID";



    // KYC Case Opening Subscriber to Customer KYC API M2M
    public const string CLIENT_NAME_FOR_IDP_FOR_KYC_CASE_OPENING_SUBSCRIBER_TO_CUST_KYC_API_M2M = "KYC Case Opening Subscriber to Customer KYC API M2M Client";

    public const string CLIENT_ID_FOR_IDP_FOR_KYC_CASE_OPENING_SUBSCRIBER_TO_CUST_KYC_API_M2M = "CustomerKyc.CaseOpeningSubscriber.To.CustomerKycApi.M2M.ClientID";



    public const string BFF_CLIENT_BASE_URL = "https://kyc.dev.localhost:33800";

	public const string MICROSERVICE_API_BASE_URL = "https://kyc-api.dev.localhost:44305";
}