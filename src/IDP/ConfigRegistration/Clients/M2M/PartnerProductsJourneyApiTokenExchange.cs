using Duende.IdentityModel;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

public sealed class PartnerProductsJourneyApiTokenExchange
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = ClientSettings.Ids.PartnerProductsJourneyApi,
            ClientName = "Partner Products Journey API Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.PartnerProductsJourneyApi) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Token exchange ONLY - no client credentials: the Partner Products Journey API can
            // │ never call another API as itself, only for the signed-in staff member, whose own
            // │ permissions and branch travel with every token. Exactly the scopes its screens need:
            // │ its own Domain API, the customer (read), the paying accounts (read), the scan of
            // │ the signed form (write) and the purchase's payment (payments.write: the Payments
            // │ API still needs the PERSON's payment.initiate and branch - the staff member pays).
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            AllowedScopes =
            {
                "partner-products.read", "partner-products.write",
                "customer-onboarding.read",
                "accounts.read",
                "documents-management.write",
                "payments.write"
            },

            // Delegated tokens are short-lived: one request, one call.
            AccessTokenLifetime = 300
        };
}