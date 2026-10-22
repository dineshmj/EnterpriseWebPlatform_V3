using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

/// <summary>
/// The customers' channel's Journey API ("My EWP"). It receives DELEGATED customer tokens only
/// (token exchange by the My EWP web BFF): the customer number, no staff claim.
/// </summary>
public sealed class SelfServiceApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "self-service-journey-api",
                "Self-Service Journey API")
        {
            Scopes = { "self-service-journey.read" },
            UserClaims = { "name", "customer_number" },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ FAPI 2.0 does not allow RS256: the tokens this API receives are signed PS256.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedAccessTokenSigningAlgorithms = { "PS256" }
        };
}