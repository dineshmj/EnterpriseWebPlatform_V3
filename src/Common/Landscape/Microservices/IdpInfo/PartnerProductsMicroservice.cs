namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

/// <summary>
/// The Partner Products context (EWP as the Partner Bank's DISTRIBUTOR at the assisted-channel
/// counter: Digital Gold), built in the customer's pattern:
///   Next.js SPA + light BFF (server actions)  --token exchange-->  NestJS Journey API
///   --token exchange-->  Partner Products Domain API (and Customer Onboarding, Accounts and
///   Documents Management for the customer, the paying account and the form's scan).
/// Every hop authenticates as its own client AND carries the person (RFC 8693 delegation).
/// </summary>
public static class PartnerProductsMicroservice
{
	// The Next.js light BFF: signs the person in (authorization code + PKCE) and exchanges their
	// token for one aimed at the Journey API.
	public const string CLIENT_NAME_FOR_IDP = "Partner Products Microservice Web (Next.js BFF) Client";

	public const string CLIENT_ID_FOR_IDP = "PartnerProducts.Microservice.Web.ClientID";


	// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
	// │ The NestJS Journey API: may ONLY exchange a token it received from the Partner Products
	// │ web BFF (no client credentials - it can never act without a person behind it). It holds
	// │ no key of the Partner Bank's: the Partner Products API is EWP's adapter to it.
	// └──────────────────────────────────────────────────────────────────────────────────────
	public const string CLIENT_NAME_FOR_IDP_FOR_PARTNER_PRODUCTS_JOURNEY_API = "Partner Products Journey API Client";

	public const string CLIENT_ID_FOR_IDP_FOR_PARTNER_PRODUCTS_JOURNEY_API = "PartnerProducts.JourneyApi.ClientID";


	// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
	// │ The Temporal worker (the purchase workflow): a MACHINE identity, client credentials,
	// │ for the follow-up nobody waits for at the counter - reading the payment's outcome
	// │ (Payments) and sending the payment advice or the cancellation (Partner Products).
	// │ Both APIs pin this client ID on their internal endpoints; it reaches nothing else.
	// └──────────────────────────────────────────────────────────────────────────────────────
	public const string CLIENT_NAME_FOR_IDP_FOR_PARTNER_PRODUCTS_WORKER_M2M = "Partner Products Worker (Temporal) M2M Client";

	public const string CLIENT_ID_FOR_IDP_FOR_PARTNER_PRODUCTS_WORKER_M2M = "PartnerProducts.Worker.M2M.ClientID";


	public const string WEB_CLIENT_BASE_URL = "https://ewp-partner.dev.localhost:46384";

	public const string JOURNEY_API_BASE_URL = "https://partner-products-journey.dev.localhost:46385";

	public const string MICROSERVICE_API_BASE_URL = "https://partner-products-api.dev.localhost:46387";
}