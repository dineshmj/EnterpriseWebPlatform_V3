using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

/// <summary>
/// The Operations Journey API. It receives DELEGATED tokens only (token exchange by the Operations
/// web BFF): the person (role, permissions, LAN ID, and how and when they signed in - for the
/// step-up on simulator changes) plus the acting client ("act").
/// </summary>
public sealed class OperationsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "operations-journey-api",
                "Operations Journey API")
        {
            Scopes = { "operations-journey.read", "operations-journey.write" },
            UserClaims = { "role", "permission", "lan_id" }
        };
}