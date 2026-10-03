using EnterpriseWebPlatform.IdentityServer.Security;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape;

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

                    AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : The Authorization Code flow is the recommended OIDC flow for confidential server-side clients (BFFs).
                    //              It returns an authorization code to the server (via a browser redirection), which the server exchanges for tokens using its client secret.
                    //              This keeps access/refresh tokens off the browser and leverages server-side confidentiality.
                    // 🡡__ IF NOT: Using implicit or hybrid flows (or client-side flows) would expose tokens to the browser, increasing XSS risk.
                    //              If a non-confidential grant (e.g., Resource Owner Password) were used, it would require sending user credentials
                    //              to the client and reduce overall security. The client might also be unable to validate tokens or perform
                    //              secure token exchange in a standard way.
                    RequirePkce = true,


                    RedirectUris = { $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL}/signout-oidc",

                    AllowOfflineAccess = true,
                    // 🡡__ WHY   : Allowing offline access enables issuance of refresh tokens (offline access RFC). BFFs or server-side
                    //              clients can use refresh tokens to silently obtain new access tokens without forcing the user to re-authenticate.
                    //              This is important for long-lived sessions, background jobs, or UX where silent re-auth is desired.
                    // 🡡__ IF NOT: If false, refresh tokens will not be issued. Clients must prompt the user to sign in again when the access token expires,
                    //              causing more frequent interactive logins and worse UX. Background/cron operations requiring API access without user interaction
                    //              would be impossible.

                    AllowedScopes =
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.Email,
                        "roles"
                    },

                    // For the Shell application, show the content page.
                    RequireConsent = true,

                    UpdateAccessTokenClaimsOnRefresh = true,
                    // 🡡__ WHY   : Each refresh re-reads the user's CURRENT roles and ABAC attributes (e.g. branch), so an
                    //              administrative change takes effect within one access-token lifetime.
                    // 🡡__ IF NOT: Refreshed tokens keep the claims of the original sign-in until the user signs in again.

                    RefreshTokenUsage = TokenUsage.OneTimeOnly,
                    // 🡡__ WHY   : Rotation (OAuth 2.1 / RFC 9700): every refresh returns a NEW refresh token and invalidates the old one, so a
                    //              stolen refresh token stops working after its next legitimate use, and replay of a used token is detectable.
                    // 🡡__ IF NOT: With ReUse, one leaked refresh token stays valid until it expires, silently granting new access tokens.


                    RefreshTokenExpiration = TokenExpiration.Sliding,
                    SlidingRefreshTokenLifetime = 3600
                    // 🡡__ WHY   : Sliding expiration extends the refresh token lifetime (by the configured window) each time it is used, enabling
                    //              long-lived sessions for active users while expiring tokens for inactive accounts. The Sliding lifetime here (3600)
                    //              is the renewal window (in seconds) applied on each use; adjust based on desired session duration and security posture.
                    // 🡡__ IF NOT: If you use Absolute expiration instead, refresh tokens will expire after a fixed period regardless of usage,
                    //              forcing reauthentication after that period. If Sliding is misconfigured (too long) you could unintentionally enable
                    //              excessively long sessions; if too short, users will be asked to re-login frequently.

                };
        }
    }
}