namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

public static class DocumentsManagementMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Documents Management Microservice BFF Client";

	public const string CLIENT_ID_FOR_IDP = "DocumentsManagement.Microservice.BFF.ClientID";

	// Document Invalidation Subscriber to Documents Management API M2M (consumes
	// onboarding.application.rejected and invalidates the application's evidence).
	public const string CLIENT_NAME_FOR_IDP_FOR_DOCUMENT_INVALIDATION_SUBSCRIBER_TO_DOC_MGMT_API_M2M = "Document Invalidation Subscriber to Documents Management API M2M Client";

	public const string CLIENT_ID_FOR_IDP_FOR_DOCUMENT_INVALIDATION_SUBSCRIBER_TO_DOC_MGMT_API_M2M = "DocumentsManagement.InvalidationSubscriber.To.DocumentsManagementApi.M2M.ClientID";

	// No BFF / MFE: Documents Management is an API-only supporting service. The Customer
	// Onboarding and KYC BFFs call it for the signed-in person (token exchange, RFC 8693).
	public const string MICROSERVICE_API_BASE_URL = "https://documents-management-api.dev.localhost:49486";
}