using Duende.IdentityModel;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

public sealed class SelfServiceJourneyApiTokenExchange
    : IDuendeClient
{
    public static Client Client =>
        new Client
        {
            ClientId = ClientSettings.Ids.SelfServiceJourneyApi,
            ClientName = "Self-Service Journey API Client",
            ClientSecrets = { ClientKeyStore.For(ClientSettings.Ids.SelfServiceJourneyApi) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Token exchange ONLY, for a customer (never as itself, never for staff): read the
            // │ customer's own accounts and Digital Gold - nothing else. private_key_jwt instead
            // │ of a secret, and every token it gets is bound to ITS DPoP key, so a token taken
            // │ from the Journey API's memory or logs cannot be replayed by anyone else.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            RequireDPoP = true,
            AllowedScopes =
            {
                "accounts.customer.read",
                "partner-products.customer.read"
            },

            // Delegated tokens are short-lived: one request, one call.
            AccessTokenLifetime = 300
        }.ForCustomers();
}