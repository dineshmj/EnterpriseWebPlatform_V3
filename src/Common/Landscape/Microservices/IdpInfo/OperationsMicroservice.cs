namespace EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

/// <summary>
/// The Operations context (platform health and, in Development, the simulators' behaviour), built
/// in the customer's pattern:
///   Next.js SPA + light BFF (server actions)  --token exchange-->  NestJS Journey API
///   --> every component's readiness probe, the simulators' loopback-only switch, and - with a
///   second token exchange - the Accounts and Compliance APIs for their parked Outbox messages.
/// </summary>
public static class OperationsMicroservice
{
	// The Next.js light BFF: signs the person in (authorization code + PKCE) and exchanges their
	// token for one aimed at the Journey API.
	public const string CLIENT_NAME_FOR_IDP = "Operations Microservice Web (Next.js BFF) Client";

	public const string CLIENT_ID_FOR_IDP = "Operations.Microservice.Web.ClientID";

	// The NestJS Journey API: may ONLY exchange a token it received from the Operations web BFF (no
	// client credentials - it can never act without a person behind it).
	public const string CLIENT_NAME_FOR_IDP_FOR_OPERATIONS_JOURNEY_API = "Operations Journey API Client";

	public const string CLIENT_ID_FOR_IDP_FOR_OPERATIONS_JOURNEY_API = "Operations.JourneyApi.ClientID";


	public const string WEB_CLIENT_BASE_URL = "https://operations.dev.localhost:46381";

	public const string JOURNEY_API_BASE_URL = "https://operations-journey.dev.localhost:46382";
}