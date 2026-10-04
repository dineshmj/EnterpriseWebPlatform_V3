using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

public sealed class MfeCompliance
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Compliance Microservice Client (BFF using ASP.NET Core 10)
                new()
                {
                    ClientId = ComplianceMicroservice.CLIENT_ID_FOR_IDP,
                    ClientName = ComplianceMicroservice.CLIENT_NAME_FOR_IDP,
                    ClientSecrets = { ClientSecretStore.For(ComplianceMicroservice.CLIENT_ID_FOR_IDP) },

                    AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : A confidential BFF keeps the tokens on the server; the browser holds only a session cookie.
                    // 🡡__ IF NOT: A browser-based flow would expose access tokens to JavaScript (XSS token theft).

                    RequirePkce = true,

                    RedirectUris = { $"{ComplianceMicroservice.BFF_CLIENT_BASE_URL}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{ComplianceMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{ComplianceMicroservice.BFF_CLIENT_BASE_URL}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{ComplianceMicroservice.BFF_CLIENT_BASE_URL}/bff/backchannel",
                    BackChannelLogoutSessionRequired = true,

                    AllowOfflineAccess = true,
                    // 🡡__ WHY   : Refresh tokens let the BFF renew the user's access token while the officer works a case.
                    // 🡡__ IF NOT: The officer would have to sign in again whenever the access token expires.

                    AllowedScopes =
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.Email,
                        "roles",
                        "organization",
                        ComplianceApiScopesRequired.COMPLIANCE_READ,
                        ComplianceApiScopesRequired.COMPLIANCE_WRITE
                    },

                    UpdateAccessTokenClaimsOnRefresh = true,
                    // 🡡__ WHY   : Each refresh re-reads the officer's CURRENT roles and ABAC attributes (branch, clearance),
                    //              so an administrative change takes effect within one access-token lifetime.
                    // 🡡__ IF NOT: Refreshed tokens keep the claims of the original sign-in until the officer signs in again.

                    RefreshTokenUsage = TokenUsage.OneTimeOnly,
                    // 🡡__ WHY   : Rotation (OAuth 2.1 / RFC 9700): a stolen refresh token stops working after its next legitimate use.
                    // 🡡__ IF NOT: One leaked refresh token stays valid until it expires.

                    RefreshTokenExpiration = TokenExpiration.Sliding,
                    SlidingRefreshTokenLifetime = 3600
                };
        }
    }
}