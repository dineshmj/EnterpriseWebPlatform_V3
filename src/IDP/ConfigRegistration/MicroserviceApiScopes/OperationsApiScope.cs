using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

/// <summary>The Operations Journey API (the Next.js BFF calls it for the signed-in operations administrator).</summary>
public static class OperationsApiScope
{
    public static ApiScope JourneyRead =>
        new(
                OperationsApiScopesRequired.OPERATIONS_JOURNEY_READ,
                "Operations Journey API - Read")
        {
            UserClaims = { "role", "name" }
        };

    public static ApiScope JourneyWrite =>
        new(
                OperationsApiScopesRequired.OPERATIONS_JOURNEY_WRITE,
                "Operations Journey API - Write")
        {
            UserClaims = { "role", "name" }
        };
}