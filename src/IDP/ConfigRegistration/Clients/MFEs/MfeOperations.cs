using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

/// <summary>
/// The Operations front end's light BFF (Next.js server side, the customer's pattern). It signs
/// the person in like every other BFF, then - for each call to the Journey API - exchanges the
/// person's token for a short-lived one aimed at the Journey API (RFC 8693 delegation).
/// </summary>
public sealed class MfeOperations
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = OperationsMicroservice.CLIENT_ID_FOR_IDP,
            ClientName = OperationsMicroservice.CLIENT_NAME_FOR_IDP,
            ClientSecrets = { ClientSecretStore.For(OperationsMicroservice.CLIENT_ID_FOR_IDP) },

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Sign-in (Authorization Code + PKCE, tokens on the server) plus token exchange (RFC
            // │ 8693): the exchanged token names this BFF as the acting client, so the Journey API
            // │ knows which application calls for the person.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { GrantType.AuthorizationCode, OidcConstants.GrantTypes.TokenExchange },

            RequirePkce = true,

            RedirectUris = { $"{OperationsMicroservice.WEB_CLIENT_BASE_URL}/api/auth/callback" },
            PostLogoutRedirectUris = { $"{OperationsMicroservice.WEB_CLIENT_BASE_URL}/" },
            FrontChannelLogoutUri = $"{OperationsMicroservice.WEB_CLIENT_BASE_URL}/api/auth/frontchannel-logout",
            BackChannelLogoutUri = $"{OperationsMicroservice.WEB_CLIENT_BASE_URL}/api/auth/backchannel-logout",
            BackChannelLogoutSessionRequired = true,

            AllowOfflineAccess = true,
            RefreshTokenUsage = TokenUsage.OneTimeOnly,
            RefreshTokenExpiration = TokenExpiration.Sliding,
            SlidingRefreshTokenLifetime = 3600,
            UpdateAccessTokenClaimsOnRefresh = true,

            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                "roles",
                "organization",
                // Requested only in the token exchange, never at sign-in.
                OperationsApiScopesRequired.OPERATIONS_JOURNEY_READ,
                OperationsApiScopesRequired.OPERATIONS_JOURNEY_WRITE
            }
        };
}