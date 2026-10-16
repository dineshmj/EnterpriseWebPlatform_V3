using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

/// <summary>The Partner Products context: its Domain API (read, write) and its Journey API (read, write).</summary>
public static class PartnerProductsApiScope
{
    public static ApiScope Read =>
        new(
                PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_READ,
                "Partner Products API - Read")
        {
            UserClaims = { "role", "name" }
        };

    public static ApiScope Write =>
        new(
                PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_WRITE,
                "Partner Products API - Write")
        {
            UserClaims = { "role", "name" }
        };

    public static ApiScope JourneyRead =>
        new(
                PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_JOURNEY_READ,
                "Partner Products Journey API - Read")
        {
            UserClaims = { "role", "name" }
        };

    public static ApiScope JourneyWrite =>
        new(
                PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_JOURNEY_WRITE,
                "Partner Products Journey API - Write")
        {
            UserClaims = { "role", "name" }
        };
}