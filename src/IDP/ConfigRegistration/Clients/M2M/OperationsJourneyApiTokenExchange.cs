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
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.OperationsJourneyApi) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Token exchange ONLY - no client credentials: the Operations Journey API can never call
            // │ Accounts, Compliance or Partner Products as itself, only for the signed-in operator -
            // │ whose own permissions (outbox.view, workflow.reprocess) and MFA freshness travel with
            // │ the token. Partner Products accepts it on the Outbox endpoints only (its policies pin
            // │ the acting client per endpoint), never on the counter's Digital Gold endpoints.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            AllowedScopes =
            {
                "accounts.read", "accounts.write",
                "compliance.read", "compliance.write",
                "partner-products.read", "partner-products.write"
            },

            // Delegated tokens are short-lived: one request, one call.
            AccessTokenLifetime = 300
        };
}