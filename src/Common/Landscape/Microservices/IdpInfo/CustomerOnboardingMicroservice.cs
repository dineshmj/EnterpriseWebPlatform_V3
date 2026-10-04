namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

public static class CustomerOnboardingMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Customer Onboarding Microservice BFF Client";

	public const string CLIENT_ID_FOR_IDP = "CustomerOnboarding.Microservice.BFF.ClientID";

	// M2M: the Onboarding Outcome Subscriber (consumes kyc.case.* and records the
	// outcome on the onboarding application through the Customer Onboarding API).
	public const string CLIENT_NAME_FOR_IDP_FOR_ONBOARDING_OUTCOME_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M = "Onboarding Outcome Subscriber to Customer Onboarding API M2M Client";
	public const string CLIENT_ID_FOR_IDP_FOR_ONBOARDING_OUTCOME_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M = "CustomerOnboarding.OutcomeSubscriber.To.CustomerOnboardingApi.M2M.ClientID";


	public const string BFF_CLIENT_BASE_URL = "https://customer.dev.localhost:44311";

	public const string MICROSERVICE_API_BASE_URL = "https://customer-api.dev.localhost:44363";
}