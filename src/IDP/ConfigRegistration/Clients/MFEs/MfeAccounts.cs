using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
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
                // Accounts Microservice Client (BFF using NestJS, and not ASP.NET Core 10).
                new()
                {
                    ClientId = AccountsMicroservice.CLIENT_ID_FOR_IDP,
                    ClientName = AccountsMicroservice.CLIENT_NAME_FOR_IDP,
                    ClientSecrets = { ClientSecretStore.For(AccountsMicroservice.CLIENT_ID_FOR_IDP) },

                    AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : The Accounts microservice (if acting as a confidential client or BFF) should use Authorization Code to keep tokens
                    //              private on the server and to benefit from the standard OIDC/OAuth flow, including PKCE if applicable.
                    // 🡡__ IF NOT: Using non-confidential or browser flows could expose tokens to the client-side, allowing token theft via XSS
                    //              and making secure API access more difficult to enforce.

                    RequirePkce = true,

                    RedirectUris = { $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/api/auth/callback" },
                    PostLogoutRedirectUris = { $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                    FrontChannelLogoutUri = $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/signout-oidc",

                    AllowOfflineAccess = true,
                    // 🡡__ WHY   : Accounts Microservice BFF frontend may need refresh tokens to maintain backend sessions or to act on behalf of the user without interactive login.
                    //              For server-to-server or long-running operations, refresh tokens enable seamless token renewal.
                    // 🡡__ IF NOT: Without offline access, the service cannot obtain refresh tokens and must force users to re-authenticate when access tokens expire.


                    AllowedScopes =
                    {
                        IdentityServerConstants.StandardScopes.OpenId,
                        IdentityServerConstants.StandardScopes.Profile,
                        IdentityServerConstants.StandardScopes.Email,
                        "roles",
                        MicroserviceApiResourceNames.ACCOUNTS_API
                            // 🡡__ WHY   : Including the ACCOUNTS_API scope permits the Accounts Microservice BFF client to request access tokens that include scope permissions for the
                            //              Accounts Microservice API. The Accounts Microservice API will validate the access token and require the corresponding scope to authorize API calls.
                            // 🡡__ IF NOT: If this scope is not included, tokens issued to the client will not be valid for calling the Accounts Microservice API, so Accounts Microservice API calls
                            //              will be denied (insufficient scope). The microservice would not be authorized to access protected endpoints.
                
                    },

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