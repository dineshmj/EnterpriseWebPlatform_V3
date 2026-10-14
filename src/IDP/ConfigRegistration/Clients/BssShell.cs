using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients;

public sealed class BssClient
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Shell BFF Client (BFF using ASP.NET Core 10)
                new()
                {
                    ClientId = BSSShellBFF.CLIENT_ID_FOR_IDP,
                    ClientName = BSSShellBFF.CLIENT_NAME_FOR_IDP,
                    ClientSecrets = { ClientSecretStore.For(BSSShellBFF.CLIENT_ID_FOR_IDP) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Authorization Code (+ PKCE) for a confidential BFF: tokens are issued to the server,
                    // │ never to the browser (no implicit or hybrid flow).
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = GrantTypes.Code,
                    RequirePkce = true,


                    RedirectUris = { $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL}/bff/backchannel",
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

                        // Welcome screen: the person's branch and LAN ID (display only; the Shell makes no
                        // authorization decision with them).
                        "organization",

                        // Notifications: the Shell shows the person's notifications (bell, live via SignalR).
                        NotificationsApiScopesRequired.NOTIFICATIONS_READ,
                        NotificationsApiScopesRequired.NOTIFICATIONS_WRITE
                    },

                    // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
                    // │ No consent page: the Shell is the bank's own first-party staff portal, not a
                    // │ third-party app asking for the person's data - access is decided by roles and
                    // │ policies, so a consent screen added nothing and reappeared on every new sign-in.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    RequireConsent = false,

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
                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Sliding refresh lifetime (one hour per use): active people stay signed in, idle
                    // │ sessions expire.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    SlidingRefreshTokenLifetime = 3600

                };
        }
    }
}