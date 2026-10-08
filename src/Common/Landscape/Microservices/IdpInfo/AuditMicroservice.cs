namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

/// <summary>
/// The Audit context, built in the customer's pattern:
///   Next.js SPA + light BFF (server actions)  --token exchange-->  NestJS Journey API
///   --token exchange-->  Audit Domain API (and other Domain APIs, e.g. Payments).
/// Every hop authenticates as its own client AND carries the person (RFC 8693 delegation).
/// </summary>
public static class AuditMicroservice
{
	// The Next.js light BFF: signs the person in (authorization code + PKCE) and exchanges their
	// token for one aimed at the Journey API.
	public const string CLIENT_NAME_FOR_IDP = "Audit Microservice Web (Next.js BFF) Client";

	public const string CLIENT_ID_FOR_IDP = "Audit.Microservice.Web.ClientID";


	// The NestJS Journey API: may ONLY exchange a token it received from the Audit web BFF (no
	// client credentials - it can never act without a person behind it).
	public const string CLIENT_NAME_FOR_IDP_FOR_AUDIT_JOURNEY_API = "Audit Journey API Client";

	public const string CLIENT_ID_FOR_IDP_FOR_AUDIT_JOURNEY_API = "Audit.JourneyApi.ClientID";


	public const string WEB_CLIENT_BASE_URL = "https://audit.dev.localhost:46380";

	public const string JOURNEY_API_BASE_URL = "https://audit-journey.dev.localhost:46379";

	public const string MICROSERVICE_API_BASE_URL = "https://audit-api.dev.localhost:46378";
}