using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

public sealed class MfeCustomerKyc
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // CustomerKyc Microservice Client (BFF using NestJS, and not ASP.NET Core 10).
                new()
                {
                    ClientId = CustomerKycMicroservice.CLIENT_ID_FOR_IDP,
                    ClientName = CustomerKycMicroservice.CLIENT_NAME_FOR_IDP,
                    ClientSecrets = { ClientSecretStore.For(CustomerKycMicroservice.CLIENT_ID_FOR_IDP) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Authorization Code (+ PKCE) for the BFF, plus token exchange (RFC 8693) for Documents
                    // │ Management (read - the officer views the evidence), always acting for the signed-in
                    // │ person.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = { GrantType.AuthorizationCode, OidcConstants.GrantTypes.TokenExchange },
                    RequirePkce = true,

                    RedirectUris = { $"{CustomerKycMicroservice.BFF_CLIENT_BASE_URL}/api/auth/callback" },
                    PostLogoutRedirectUris = { $"{CustomerKycMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{CustomerKycMicroservice.BFF_CLIENT_BASE_URL}/signout-oidc",
                    // Back-channel logout (server-to-server; NestJS BackChannelLogoutController):
                    // ends the BFF session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{CustomerKycMicroservice.BFF_CLIENT_BASE_URL}/backchannel-logout",
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
                        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                        // │ API scopes: only what this BFF's screens need; the Documents Management scope is
                        // │ requested only in the token exchange, never at sign-in.
                        // └──────────────────────────────────────────────────────────────────────────────────────
                        MicroserviceApiResourceNames.CUSTOMER_KYC_API,
                        CustomerKycApiScopesRequired.CUSTOMER_KYC_READ,
                        CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE,
                        // Requested only in the token exchange, never at sign-in.
                        DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_READ
                
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
                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Sliding refresh lifetime (one hour per use): active people stay signed in, idle
                    // │ sessions expire.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    SlidingRefreshTokenLifetime = 3600
                };
        }
    }
}