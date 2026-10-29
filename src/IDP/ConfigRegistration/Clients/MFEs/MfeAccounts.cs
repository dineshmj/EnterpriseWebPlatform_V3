using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
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
                    ClientId = ClientSettings.Ids.AccountsWeb,
                    ClientName = "Accounts Microservice BFF Client",
                    ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.AccountsWeb) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Authorization Code (+ PKCE) for a confidential BFF: tokens are issued to the server,
                    // │ never to the browser (no implicit or hybrid flow).
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = GrantTypes.Code,

                    RequirePkce = true,

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ PAR (RFC 9126, FAPI 2.0): the BFF pushes the authorization request to the IDP over the
                    // │ back channel, authenticated; the browser carries only a one-time request_uri (60 s), so
                    // │ no one can read or change the request on its way (scopes, redirect, PKCE challenge).
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    RequirePushedAuthorization = true,
                    PushedAuthorizationLifetime = 60,

                    RedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.AccountsWeb))}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.AccountsWeb))}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.AccountsWeb))}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.AccountsWeb))}/bff/backchannel",
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
                        "accounts.read",
                        "accounts.write"
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