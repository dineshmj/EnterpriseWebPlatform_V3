using Duende.IdentityModel;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

public sealed class OperationsJourneyApiTokenExchange
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = ClientSettings.Ids.OperationsJourneyApi,
            ClientName = "Operations Journey API Client",
            ClientSecrets = { ClientKeyStore.For(ClientSettings.Ids.OperationsJourneyApi) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Token exchange ONLY - no client credentials: the Operations Journey API can never call
            // │ Accounts, Compliance or Partner Products as itself, only for the signed-in operator -
            // │ whose own permissions (outbox.view, workflow.reprocess, personal-data.erasure.*) and
            // │ MFA freshness travel with the token. Partner Products accepts it on the Outbox
            // │ endpoints only (its policies pin the acting client per endpoint), never on the
            // │ counter's Digital Gold endpoints; Customer Onboarding on the erasure endpoints only.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ private_key_jwt and DPoP (FAPI 2.0): the Journey API proves itself with a one-time
            // │ assertion signed by its own key (the IDP holds only the public key - no shared
            // │ secret), and every token it gets is bound to ITS own DPoP key, so a token taken
            // │ from the Journey API's memory or logs cannot be replayed by anyone else.
            // └──────────────────────────────────────────────────────────────────────────────────────
            RequireDPoP = true,
            AllowedScopes =
            {
                "accounts.read", "accounts.write",
                "compliance.read", "compliance.write",
                "partner-products.read", "partner-products.write",
                "customer-onboarding.read", "customer-onboarding.write"
            },

            // Delegated tokens are short-lived: one request, one call.
            AccessTokenLifetime = 300
        };
}