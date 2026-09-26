using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

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
                    ClientSecrets = { new Secret(CustomerKycMicroservice.CLIENT_SECRET_FOR_IDP.Sha256()) },

                    AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : The CustomerKyc microservice (if acting as a confidential client or BFF) should use Authorization Code to keep tokens
                    //              private on the server and to benefit from the standard OIDC/OAuth flow, including PKCE if applicable.
                    // 🡡__ IF NOT: Using non-confidential or browser flows could expose tokens to the client-side, allowing token theft via XSS
                    //              and making secure API access more difficult to enforce.
                    RequirePkce = true,

                    RedirectUris = { $"{CustomerKycMicroservice.BFF_CLIENT_BASE_URL}/api/auth/callback" },
                    PostLogoutRedirectUris = { $"{CustomerKycMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{CustomerKycMicroservice.BFF_CLIENT_BASE_URL}/signout-oidc",

                    AllowOfflineAccess = true,
                    // 🡡__ WHY   : CustomerKyc Microservice BFF frontend may need refresh tokens to maintain backend sessions or to act on behalf of the user without interactive login.
                    //              For server-to-server or long-running operations, refresh tokens enable seamless token renewal.
                    // 🡡__ IF NOT: Without offline access, the service cannot obtain refresh tokens and must force users to re-authenticate when access tokens expire.


                    AllowedScopes =
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.Email,
                        "roles",
                        MicroserviceApiResourceNames.CUSTOMER_KYC_API
                            // 🡡__ WHY   : Including the CUSTOMER_KYC_API scope permits the CustomerKyc Microservice BFF client to request access tokens that include scope permissions for the
                            //              CustomerKyc Microservice API. The CustomerKyc Microservice API will validate the access token and require the corresponding scope to authorize API calls.
                            // 🡡__ IF NOT: If this scope is not included, tokens issued to the client will not be valid for calling the CustomerKyc Microservice API, so CustomerKyc Microservice API calls
                            //              will be denied (insufficient scope). The microservice would not be authorized to access protected endpoints.
                
                    },

                    RefreshTokenUsage = TokenUsage.ReUse,
                    // 🡡__ WHY   : ReUse simplifies server-side handling for refresh tokens and avoids the need to implement rotation/one-time logic.
                    //              Use ReUse for scenarios where the refresh token is stored securely and where you prefer simpler lifecycle management.
                    // 🡡__ IF NOT: OneTimeOnly (rotation) would increase security by invalidating refresh tokens after use, but requires additional server-side
                    //              bookkeeping and careful handling of concurrent refresh requests.

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