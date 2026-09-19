namespace EnterpriseWebPlatform.Common.Landscape.Microservices;

public static class CustomerOnboardingMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Customer Onboarding Microservice BFF Client";
	public const string CLIENT_ID_FOR_IDP = "CustomerOnboarding.Microservice.BFF.ClientID";
	public const string CLIENT_SECRET_FOR_IDP = "68fdf186-0157-4759-a633-441c5f5ac942";     // Random GUID for demo purposes only.

	public const string BFF_CLIENT_BASE_URL = "https://customer.dev.localhost:44311";
	public const string MICROSERVICE_API_BASE_URL = "https://customer-api.dev.localhost:44363";
}