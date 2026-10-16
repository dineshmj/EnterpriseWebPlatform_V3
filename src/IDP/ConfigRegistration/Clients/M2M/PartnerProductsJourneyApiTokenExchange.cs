using Duende.IdentityModel;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

public sealed class PartnerProductsJourneyApiTokenExchange
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = PartnerProductsMicroservice.CLIENT_ID_FOR_IDP_FOR_PARTNER_PRODUCTS_JOURNEY_API,
            ClientName = PartnerProductsMicroservice.CLIENT_NAME_FOR_IDP_FOR_PARTNER_PRODUCTS_JOURNEY_API,
            ClientSecrets = { ClientSecretStore.For(PartnerProductsMicroservice.CLIENT_ID_FOR_IDP_FOR_PARTNER_PRODUCTS_JOURNEY_API) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Token exchange ONLY - no client credentials: the Partner Products Journey API can
            // │ never call another API as itself, only for the signed-in staff member, whose own
            // │ permissions and branch travel with every token. Exactly the scopes its screens need:
            // │ its own Domain API, the customer (read), the paying accounts (read) and the scan of
            // │ the signed form (write).
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            AllowedScopes =
            {
                PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_READ, PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_WRITE,
                CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ,
                AccountsApiScopesRequired.ACCOUNTS_READ,
                DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE
            },

            // Delegated tokens are short-lived: one request, one call.
            AccessTokenLifetime = 300
        };
}