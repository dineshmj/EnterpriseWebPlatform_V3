using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.Customers;

// ┌─ :: SECURITY :: "My EWP" - the customers' channel, on the FAPI 2.0 Security Profile ───────────
// │ Why : A customer channel faces the internet and holds a bank customer's session: the
// │       authorization request must not be readable or changeable on the way, the client must
// │       prove itself without a shared secret, and a stolen token must be useless.
// │ How : PAR required (the browser carries only a one-time request_uri, 60 s); PKCE S256;
// │       private_key_jwt (ES256, only the public key here); DPoP-bound access AND refresh tokens
// │       (RFC 9449 - the BFF's per-session key); ID and access tokens signed PS256 (FAPI 2.0 does
// │       not allow RS256); 60-second codes; one-time refresh tokens; the IDP session reused at
// │       most 15 minutes. Customers only (ClientAudience), never a staff sign-in.
// │ Ref : Features 2.1.6 | FAPI 2.0 Security Profile §5.3 | RFC 9126 (PAR), 7636 (PKCE), 7523,
// │       9449 (DPoP), 9207 (iss) | OWASP A07 | Threat-Model R-17
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// The customers' own web channel ("My EWP": Next.js SPA + light BFF). It signs the customer in,
/// then - for each call - exchanges the customer's token for a short-lived, DPoP-bound one aimed
/// at the Self-Service Journey API (RFC 8693 delegation).
/// </summary>
public sealed class SelfServiceWeb
    : IDuendeClient
{
    public static Client Client =>
        new Client
        {
            ClientId = ClientSettings.Ids.SelfServiceWeb,
            ClientName = "My EWP",
            ClientSecrets = { ClientKeyStore.For(ClientSettings.Ids.SelfServiceWeb) },

            AllowedGrantTypes = { GrantType.AuthorizationCode, OidcConstants.GrantTypes.TokenExchange },
            RequirePkce = true,
            RequirePushedAuthorization = true,
            PushedAuthorizationLifetime = 60,
            AuthorizationCodeLifetime = 60,

            RequireDPoP = true,
            AllowedIdentityTokenSigningAlgorithms = { "PS256" },

            RedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.SelfServiceWeb))}/api/auth/callback" },
            PostLogoutRedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.SelfServiceWeb))}/" },
            BackChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.SelfServiceWeb))}/api/auth/backchannel-logout",
            BackChannelLogoutSessionRequired = true,

            RequireConsent = false,
            UserSsoLifetime = 900,

            AllowOfflineAccess = true,
            RefreshTokenUsage = TokenUsage.OneTimeOnly,
            RefreshTokenExpiration = TokenExpiration.Sliding,
            SlidingRefreshTokenLifetime = 900,
            AbsoluteRefreshTokenLifetime = 8 * 3600,
            UpdateAccessTokenClaimsOnRefresh = true,
            AccessTokenLifetime = 300,

            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                "customer",
                // Requested only in the token exchange, never at sign-in.
                "self-service-journey.read"
            }
        }.ForCustomers();
}