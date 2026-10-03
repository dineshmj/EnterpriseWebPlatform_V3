using EnterpriseWebPlatform.IdentityServer.Security;
using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

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

                    AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : The CustomerOnboarding microservice (if acting as a confidential client or BFF) should use Authorization Code to keep tokens
                    //              private on the server and to benefit from the standard OIDC/OAuth flow, including PKCE if applicable.
                    // 🡡__ IF NOT: Using non-confidential or browser flows could expose tokens to the client-side, allowing token theft via XSS
                    //              and making secure API access more difficult to enforce.

                    RequirePkce = true,

                    RedirectUris = { $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/signin-oidc" },
                    PostLogoutRedirectUris = { $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/signout-oidc",
                    // Back-channel logout (server-to-server; Duende BFF endpoint): ends the
                    // server-side session even when the browser blocks the front-channel iframe.
                    BackChannelLogoutUri = $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/bff/backchannel",
                    BackChannelLogoutSessionRequired = true,

                    AllowOfflineAccess = true,
                    // 🡡__ WHY   : CustomerOnboarding Microservice BFF frontend may need refresh tokens to maintain backend sessions or to act on behalf of the user without interactive login.
                    //              For server-to-server or long-running operations, refresh tokens enable seamless token renewal.
                    // 🡡__ IF NOT: Without offline access, the service cannot obtain refresh tokens and must force users to re-authenticate when access tokens expire.


                    AllowedScopes =
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.Email,
                        "roles",
                        "organization",
                        MicroserviceApiResourceNames.CUSTOMER_ONBOARDING_API,
                            // 🡡__ WHY   : Including the CUSTOMER_ONBOARDING_API scope permits the CustomerOnboarding Microservice BFF client to request access tokens that include scope permissions for the
                            //              CustomerOnboarding Microservice API. The CustomerOnboarding Microservice API will validate the access token and require the corresponding scope to authorize API calls.
                            // 🡡__ IF NOT: If this scope is not included, tokens issued to the client will not be valid for calling the CustomerOnboarding Microservice API, so CustomerOnboarding Microservice API calls
                            //              will be denied (insufficient scope). The microservice would not be authorized to access protected endpoints.
                        CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ,
                        CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE
                    },

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
                    // 🡡__ WHY   : Sliding expiration helps keep active users authenticated without forcing frequent full re-authentication. The value
                    //              of 3600 seconds establishes the sliding window; each successful refresh within that window extends validity.
                    // 🡡__ IF NOT: Absolute expiration would set a hard timeout after which the refresh token is invalid regardless of usage. If sliding
                    //              is omitted and tokens are short-lived, clients must reauthenticate more often.            
                };
        }
    }
}