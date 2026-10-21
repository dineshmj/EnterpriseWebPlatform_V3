using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

/// <summary>The Operations Journey API (the Next.js BFF calls it for the signed-in operations administrator).</summary>
public static class OperationsApiScope
{
    public static ApiScope JourneyRead =>
        new(
                "operations-journey.read",
                "Operations Journey API - Read")
        {
            UserClaims = { "role", "name" }
        };

    public static ApiScope JourneyWrite =>
        new(
                "operations-journey.write",
                "Operations Journey API - Write")
        {
            UserClaims = { "role", "name" }
        };
}