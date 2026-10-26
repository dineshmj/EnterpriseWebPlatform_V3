using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

public sealed class MfePayments
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Payments Microservice Client (BFF using ASP.NET Core 10)
                new()
                {
                    ClientId = ClientSettings.Ids.PaymentsWeb,
                    ClientName = "Payments Microservice BFF Client",
                    ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.PaymentsWeb) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Authorization Code (+ PKCE) for a confidential BFF: tokens are issued to the server,
                    // │ never to the browser (no implicit or hybrid flow).
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = GrantTypes.Code,

                    RequirePkce = true,

                    RedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.PaymentsWeb))}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{ClientSettings.BaseUrlOf(nameof(ClientIds.PaymentsWeb))}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.PaymentsWeb))}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{ClientSettings.BaseUrlOf(nameof(ClientIds.PaymentsWeb))}/bff/backchannel",
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
                        "payments.read",
                        "payments.write",

                        // The payment screen finds the customer's paying accounts (and what is
                        // available) in Accounts, with the staff member's own token.
                        "accounts.read",

                        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                        // │ The counter's cash deposit credits an account in Accounts. accounts.write alone
                        // │ opens nothing else: the Accounts API admits this client (pinned by client ID)
                        // │ only to cash deposits, and only for a person with account.deposit.record.
                        // └──────────────────────────────────────────────────────────────────────────────────────
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