namespace EnterpriseWebPlatform.Common.Landscape.Microservices
{
	public static class PaymentsMicroservice
	{
		public const string CLIENT_NAME_FOR_IDP = "Payments Microservice BFF Client";
		public const string CLIENT_ID_FOR_IDP = "Payments.Microservice.BFF.ClientID";
		public const string CLIENT_SECRET_FOR_IDP = "439efc7b-7040-484a-bbb7-5278b8b061b2";     // Random GUID for demo purposes only.

		public const string BFF_CLIENT_BASE_URL = "https://payments.dev.localhost:44388";
		public const string MICROSERVICE_API_BASE_URL = "https://payments-api.dev.localhost:44488";
    }
}