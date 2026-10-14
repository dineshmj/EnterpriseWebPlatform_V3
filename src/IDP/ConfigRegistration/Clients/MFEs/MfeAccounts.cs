using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

public sealed class MfeAccounts
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Accounts Microservice Client (BFF using ASP.NET Core 10)
                new()
                {
                    ClientId = AccountsMicroservice.CLIENT_ID_FOR_IDP,
                    ClientName = AccountsMicroservice.CLIENT_NAME_FOR_IDP,
                    ClientSecrets = { ClientSecretStore.For(AccountsMicroservice.CLIENT_ID_FOR_IDP) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Authorization Code (+ PKCE) for a confidential BFF: tokens are issued to the server,
                    // │ never to the browser (no implicit or hybrid flow).
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = GrantTypes.Code,

                    RequirePkce = true,

                    RedirectUris = { $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/bff/backchannel",
                    BackChannelLogoutSessionRequired = true,

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Refresh tokens for the BFF (offline_access), so the session is renewed without a new
                    // │ sign-in.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowOfflineAccess = true,

                    AllowedScopes =
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.Email,
                        "roles",
                        "organization",
                        AccountsApiScopesRequired.ACCOUNTS_READ,
                        AccountsApiScopesRequired.ACCOUNTS_WRITE
                    },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Each refresh re-reads the CURRENT roles and ABAC attributes (branch, clearance), so
                    // │ an administrative change takes effect within one access-token lifetime.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    UpdateAccessTokenClaimsOnRefresh = true,

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Refresh-token rotation (OAuth 2.1 / RFC 9700): every refresh returns a new token and
                    // │ invalidates the old one - a stolen refresh token dies at its next legitimate use.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    RefreshTokenUsage = TokenUsage.OneTimeOnly,

                    RefreshTokenExpiration = TokenExpiration.Sliding,
                    SlidingRefreshTokenLifetime = 3600
                };
        }
    }
}