using Duende.IdentityModel;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// The Audit Journey API's machine identity. Its ONLY grant is token exchange: it can get a token
/// for the Audit API (and the Payments API, for a payment's current status) solely by presenting
/// a token the Audit web BFF obtained for a signed-in person. There is no client-credentials
/// grant, so it can never call a Domain API without a person behind it.
/// </summary>
public sealed class AuditJourneyApiTokenExchange
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = ClientSettings.Ids.AuditJourneyApi,
            ClientName = "Audit Journey API Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.AuditJourneyApi) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Token exchange ONLY - no client credentials: the Audit Journey API can never call a
            // │ Domain API as itself, only for a person (read scopes only).
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            AllowedScopes = { "audit.read", "payments.read" },

            // Delegated tokens are short-lived: one journey, a few calls.
            AccessTokenLifetime = 300
        };
}