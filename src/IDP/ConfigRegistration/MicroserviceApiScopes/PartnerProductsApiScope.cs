using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

/// <summary>The Partner Products context: its Domain API (read, write) and its Journey API (read, write).</summary>
public static class PartnerProductsApiScope
{
    public static ApiScope Read =>
        new(
                "partner-products.read",
                "Partner Products API - Read")
        {
            UserClaims = { "role", "name" }
        };

    public static ApiScope Write =>
        new(
                "partner-products.write",
                "Partner Products API - Write")
        {
            UserClaims = { "role", "name" }
        };

    public static ApiScope JourneyRead =>
        new(
                "partner-products-journey.read",
                "Partner Products Journey API - Read")
        {
            UserClaims = { "role", "name" }
        };

    public static ApiScope JourneyWrite =>
        new(
                "partner-products-journey.write",
                "Partner Products Journey API - Write")
        {
            UserClaims = { "role", "name" }
        };
}