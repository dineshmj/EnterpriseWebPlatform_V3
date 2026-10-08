using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

/// <summary>
/// The Audit front end's light BFF (Next.js server side, the customer's pattern). It signs the
/// person in like every other BFF, then - for each call to the Journey API - exchanges the
/// person's token for a short-lived one aimed at the Journey API (RFC 8693 delegation).
/// </summary>
public sealed class MfeAudit
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = AuditMicroservice.CLIENT_ID_FOR_IDP,
            ClientName = AuditMicroservice.CLIENT_NAME_FOR_IDP,
            ClientSecrets = { ClientSecretStore.For(AuditMicroservice.CLIENT_ID_FOR_IDP) },

            AllowedGrantTypes = { GrantType.AuthorizationCode, OidcConstants.GrantTypes.TokenExchange },
            // 🡡__ WHY   : Sign-in (code + PKCE, tokens kept on the server) and token exchange for the
            //              Journey API - the exchanged token names this BFF as the acting client.
            // 🡡__ IF NOT: The BFF would forward the person's own token, and the Journey API could not
            //              tell which application is calling for them.

            RequirePkce = true,

            RedirectUris = { $"{AuditMicroservice.WEB_CLIENT_BASE_URL}/api/auth/callback" },
            PostLogoutRedirectUris = { $"{AuditMicroservice.WEB_CLIENT_BASE_URL}/" },
            FrontChannelLogoutUri = $"{AuditMicroservice.WEB_CLIENT_BASE_URL}/api/auth/frontchannel-logout",
            BackChannelLogoutUri = $"{AuditMicroservice.WEB_CLIENT_BASE_URL}/api/auth/backchannel-logout",
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
                AuditApiScopesRequired.AUDIT_JOURNEY_READ
            }
        };
}