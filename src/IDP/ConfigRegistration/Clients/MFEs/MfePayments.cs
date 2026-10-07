using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
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
                    ClientId = PaymentsMicroservice.CLIENT_ID_FOR_IDP,
                    ClientName = PaymentsMicroservice.CLIENT_NAME_FOR_IDP,
                    ClientSecrets = { ClientSecretStore.For(PaymentsMicroservice.CLIENT_ID_FOR_IDP) },

                    AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : A confidential BFF keeps the tokens on the server; the browser holds only a session cookie.
                    // 🡡__ IF NOT: A browser-based flow would expose access tokens to JavaScript (XSS token theft).

                    RequirePkce = true,

                    RedirectUris = { $"{PaymentsMicroservice.BFF_CLIENT_BASE_URL}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{PaymentsMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{PaymentsMicroservice.BFF_CLIENT_BASE_URL}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{PaymentsMicroservice.BFF_CLIENT_BASE_URL}/bff/backchannel",
                    BackChannelLogoutSessionRequired = true,

                    AllowOfflineAccess = true,
                    // 🡡__ WHY   : Refresh tokens let the BFF renew the user's access token while a payment is captured or followed.
                    // 🡡__ IF NOT: The staff member would have to sign in again whenever the access token expires.

                    AllowedScopes =
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.Email,
                        "roles",
                        "organization",
                        PaymentsApiScopesRequired.PAYMENTS_READ,
                        PaymentsApiScopesRequired.PAYMENTS_WRITE,

                        // The payment screen finds the customer's paying accounts (and what is
                        // available) in Accounts, with the staff member's own token - read only.
                        AccountsApiScopesRequired.ACCOUNTS_READ
                    },

                    UpdateAccessTokenClaimsOnRefresh = true,
                    // 🡡__ WHY   : Each refresh re-reads the user's CURRENT roles and ABAC attributes (branch, clearance),
                    //              so an administrative change takes effect within one access-token lifetime.
                    // 🡡__ IF NOT: Refreshed tokens keep the claims of the original sign-in until the user signs in again.

                    RefreshTokenUsage = TokenUsage.OneTimeOnly,
                    // 🡡__ WHY   : Rotation (OAuth 2.1 / RFC 9700): a stolen refresh token stops working after its next legitimate use.
                    // 🡡__ IF NOT: One leaked refresh token stays valid until it expires.

                    RefreshTokenExpiration = TokenExpiration.Sliding,
                    SlidingRefreshTokenLifetime = 3600
                };
        }
    }
}