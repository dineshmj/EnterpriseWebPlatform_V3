namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

public static class AccountsMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Accounts Microservice BFF Client";

	public const string CLIENT_ID_FOR_IDP = "Accounts.Microservice.BFF.ClientID";


	// Account Application Opening Subscriber to Accounts API M2M (consumes
	// compliance.case.approved and opens the account application).
	public const string CLIENT_NAME_FOR_IDP_FOR_ACCOUNT_APPLICATION_OPENING_SUBSCRIBER_TO_ACCOUNTS_API_M2M = "Account Application Opening Subscriber to Accounts API M2M Client";

	public const string CLIENT_ID_FOR_IDP_FOR_ACCOUNT_APPLICATION_OPENING_SUBSCRIBER_TO_ACCOUNTS_API_M2M = "Accounts.ApplicationOpeningSubscriber.To.AccountsApi.M2M.ClientID";


	public const string BFF_CLIENT_BASE_URL = "https://accounts.dev.localhost:45456";

	public const string MICROSERVICE_API_BASE_URL = "https://accounts-api.dev.localhost:48486";
}