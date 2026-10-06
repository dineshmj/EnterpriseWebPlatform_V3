namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

public static class PaymentsMicroservice
{
	public const string CLIENT_NAME_FOR_IDP = "Payments Microservice BFF Client";

	public const string CLIENT_ID_FOR_IDP = "Payments.Microservice.BFF.ClientID";


	// Payments Saga Reply Subscriber to Payments API M2M (consumes accounts.funds.replies and
	// hands each reply to the payment saga orchestrator).
	public const string CLIENT_NAME_FOR_IDP_FOR_PAYMENTS_SAGA_REPLY_SUBSCRIBER_TO_PAYMENTS_API_M2M = "Payments Saga Reply Subscriber to Payments API M2M Client";

	public const string CLIENT_ID_FOR_IDP_FOR_PAYMENTS_SAGA_REPLY_SUBSCRIBER_TO_PAYMENTS_API_M2M = "Payments.SagaReplySubscriber.To.PaymentsApi.M2M.ClientID";


	public const string BFF_CLIENT_BASE_URL = "https://payments.dev.localhost:46388";

	public const string MICROSERVICE_API_BASE_URL = "https://payments-api.dev.localhost:44488";
}