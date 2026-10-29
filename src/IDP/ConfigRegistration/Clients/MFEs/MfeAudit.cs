using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
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
            ClientId = ClientSettings.Ids.AuditWeb,
            ClientName = "Audit Microservice Web (Next.js BFF) Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.AuditWeb) },

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Sign-in (Authorization Code + PKCE, tokens on the server) plus token exchange (RFC
            // │ 8693): the exchanged token names this BFF as the acting client, so the Journey API
            // │ knows which application calls for the person.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { GrantType.AuthorizationCode, OidcConstants.GrantTypes.TokenExchange },

            RequirePkce = true,

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ PAR (RFC 9126, FAPI 2.0): the BFF pushes the authorization request to the IDP over the
            // │ back channel, authenticated; the browser carries only a one-time request_uri (60 s), so
            // │ no one can read or change the request on its way (scopes, redirect, PKCE challenge).
            // └──────────────────────────────────────────────────────────────────────────────────────
            RequirePushedAuthorization = true,
            PushedAuthorizationLifetime = 60,

            RedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.AuditWeb))}/api/auth/callback" },
            PostLogoutRedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.AuditWeb))}/" },
            FrontChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.AuditWeb))}/api/auth/frontchannel-logout",
            BackChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.AuditWeb))}/api/auth/backchannel-logout",
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
                "audit-journey.read"
            }
        };
}