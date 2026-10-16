using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

/// <summary>
/// The Partner Products context's two APIs. Both receive DELEGATED tokens only (token exchange):
/// the person (role, permissions, LAN ID, branch - the domain API records the actor and limits
/// every list to the actor's branch) plus the acting client chain ("act").
/// </summary>
public sealed class PartnerProductsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.PARTNER_PRODUCTS_API,
                "Partner Products API")
        {
            Scopes = { PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_READ, PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_WRITE },
            UserClaims = { "role", "name", "permission", "lan_id", "branch" }
        };

    public static ApiResource JourneyApiResource =>
        new(
                MicroserviceApiResourceNames.PARTNER_PRODUCTS_JOURNEY_API,
                "Partner Products Journey API")
        {
            Scopes = { PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_JOURNEY_READ, PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_JOURNEY_WRITE },
            UserClaims = { "role", "permission", "lan_id", "branch" }
        };
}