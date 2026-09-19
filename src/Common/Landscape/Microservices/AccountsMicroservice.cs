namespace EnterpriseWebPlatform.Common.Landscape.Microservices;

public static class AccountsMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Accounts Microservice BFF Client";
	public const string CLIENT_ID_FOR_IDP = "Accounts.Microservice.BFF.ClientID";
	public const string CLIENT_SECRET_FOR_IDP = "0a431e34-8c3b-4ebe-9a6e-5e13ddc07c5b";     // Random GUID for demo purposes only.

	public const string BFF_CLIENT_BASE_URL = "https://accounts.dev.localhost:45456";
	public const string MICROSERVICE_API_BASE_URL = "https://accounts-api.dev.localhost:48486";
}