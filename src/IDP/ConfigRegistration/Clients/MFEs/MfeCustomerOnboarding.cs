using Duende.IdentityModel;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;

public sealed class MfeCustomerOnboarding
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Customer Onboarding Microservice Client (BFF using ASP.NET Core 10)
                new()
                {
                    ClientId = CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP,
                    ClientName = CustomerOnboardingMicroservice.CLIENT_NAME_FOR_IDP,
                    ClientSecrets = { ClientSecretStore.For(CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Authorization Code (+ PKCE) for the BFF, plus token exchange (RFC 8693) for Documents
                    // │ Management (write - the agent uploads the evidence), always acting for the signed-in
                    // │ person.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = { GrantType.AuthorizationCode, OidcConstants.GrantTypes.TokenExchange },

                    RequirePkce = true,

                    RedirectUris = { $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/bff/backchannel",
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
                        MicroserviceApiResourceNames.CUSTOMER_ONBOARDING_API,
                        CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ,
                        CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE,
                        // Requested only in the token exchange, never at sign-in.
                        DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE
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