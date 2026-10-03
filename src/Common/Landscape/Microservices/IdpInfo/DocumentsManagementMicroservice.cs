namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

public static class DocumentsManagementMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Documents Management Microservice BFF Client";

	public const string CLIENT_ID_FOR_IDP = "DocumentsManagement.Microservice.BFF.ClientID";



    // Customer Onboarding BFF to Documents Management API M2M
    public const string CLIENT_NAME_FOR_IDP_FOR_CUST_ONBOARDING_BFF_TO_DOC_MGMT_M2M = "Customer Onboarding BFF to Documents Management M2M Client";

    public const string CLIENT_ID_FOR_IDP_FOR_CUST_ONBOARDING_BFF_TO_DOC_MGMT_M2M = "CustomerOnboarding.BFF.To.DocumentsManagement.M2M.ClientID";


    // KYC BFF to Documents Management API M2M
    public const string CLIENT_NAME_FOR_IDP_FOR_KYC_BFF_TO_DOC_MGMT_M2M = "KYC BFF to Documents Management M2M Client";

    public const string CLIENT_ID_FOR_IDP_FOR_KYC_BFF_TO_DOC_MGMT_M2M = "Kyc.BFF.To.DocumentsManagement.M2M.ClientID";



    public const string BFF_CLIENT_BASE_URL = "https://documents-management.dev.localhost:46456";

	public const string MICROSERVICE_API_BASE_URL = "https://documents-management-api.dev.localhost:49486";
}