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
            // │ Accounts or Compliance as itself, only for the signed-in operator - whose own
            // │ permissions (outbox.view, workflow.reprocess) and MFA freshness travel with the token.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            AllowedScopes =
            {
                "accounts.read", "accounts.write",
                "compliance.read", "compliance.write"
            },

            // Delegated tokens are short-lived: one request, one call.
            AccessTokenLifetime = 300
        };
}