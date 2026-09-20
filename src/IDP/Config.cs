using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape;
using EnterpriseWebPlatform.Common.Landscape.Microservices;

public static class Config
{
    public static IEnumerable<IdentityResource> IdentityResources =>
        [
            new IdentityResources.OpenId(),
            new IdentityResources.Profile(),
            new IdentityResources.Email(),
            new (name: "roles", displayName: "User Roles", userClaims: [ "role" ])
        ];

    public static IEnumerable<ApiScope> ApiScopes =>
        [
            // Customer Onboarding API
            new(
                MicroserviceApiResources.CUSTOMER_ONBOARDING_READ,
                "Customer Onboarding API - Read")
            {
                UserClaims = { "role", "name", "email" }
            },

            new(
                MicroserviceApiResources.CUSTOMER_ONBOARDING_WRITE,
                "Customer Onboarding API - Write")
            {
                UserClaims = { "role", "name", "email" }
            },


            // Customer KYC API
            new(
                MicroserviceApiResources.CUSTOMER_KYC_READ,
                "Customer KYC API - Read")
            {
                UserClaims = { "role", "name", "email" }
            },

            new(
                MicroserviceApiResources.CUSTOMER_KYC_WRITE,
                "Customer KYC API - Write")
            {
                UserClaims = { "role", "name", "email" }
            },


            // Accounts API
            new(
                MicroserviceApiResources.ACCOUNTS_READ,
                "Accounts API - Read")
            {
                UserClaims = { "role", "name", "email" }
            },

            new(
                MicroserviceApiResources.ACCOUNTS_WRITE,
                "Accounts API - Write")
            {
                UserClaims = { "role", "name", "email" }
            },


            // Payments API
            new(
                MicroserviceApiResources.PAYMENTS_READ,
                "Payments API - Read")
            {
                UserClaims = { "role", "name", "email" }
            },

            new(
                MicroserviceApiResources.PAYMENTS_WRITE,
                "Payments API - Write")
            {
                UserClaims = { "role", "name", "email" }
            }
        ];

    public static IEnumerable<ApiResource> ApiResources =>
        [
            new(
                MicroserviceApiResources.CUSTOMER_ONBOARDING_API,
                "Customer Onboarding API")
            {
                Scopes =
                {
                    MicroserviceApiResources.CUSTOMER_ONBOARDING_READ,
                    MicroserviceApiResources.CUSTOMER_ONBOARDING_WRITE
                },

                UserClaims =
                {
                    "role",
                    "name",
                    "email"
                }
            },


            new(
                MicroserviceApiResources.CUSTOMER_KYC_API,
                "Customer KYC API")
            {
                Scopes =
                {
                    MicroserviceApiResources.CUSTOMER_KYC_READ,
                    MicroserviceApiResources.CUSTOMER_KYC_WRITE
                },

                UserClaims =
                {
                    "role",
                    "name",
                    "email"
                }
            },


            new(
                MicroserviceApiResources.ACCOUNTS_API,
                "Accounts API")
            {
                Scopes =
                {
                    MicroserviceApiResources.ACCOUNTS_READ,
                    MicroserviceApiResources.ACCOUNTS_WRITE
                },

                UserClaims =
                {
                    "role",
                    "name",
                    "email"
                }
            },


            new(
                MicroserviceApiResources.PAYMENTS_API,
                "Payments API")
            {
                Scopes =
                {
                    MicroserviceApiResources.PAYMENTS_READ,
                    MicroserviceApiResources.PAYMENTS_WRITE
                },

                UserClaims =
                {
                    "role",
                    "name",
                    "email"
                }
            }
        ];

    public static IEnumerable<Client> Clients =>
        [
            // Customer Onboarding API - Bruno OAuth 2.0 client
            new()
            {
                ClientId = "BSS.ApiTesting.Bruno.ClientID",
                ClientName = "BSS API Testing Bruno Client",

                // 🡡__ WHY   : Bruno is acting as a public client. It cannot safely keep a
                //              client secret because the OAuth client is running interactively
                //              on the developer's machine.
                // 🡡__ IF NOT: Adding a client secret would make this a confidential-client
                //              arrangement and would not provide meaningful protection for
                //              a secret distributed to a developer workstation.

                AllowedGrantTypes = GrantTypes.Code,

                // 🡡__ WHY   : Authorization Code + PKCE is the appropriate flow for a
                //              public interactive client.
                // 🡡__ IF NOT: Without PKCE, an intercepted authorization code could be
                //              exchanged by another party.

                RequirePkce = true,
                RequireClientSecret = false,

                RedirectUris =
                {
                    "https://oauth.usebruno.com/callback"
                    // "http://localhost:3000/oauth/callback"
                    // "http://127.0.0.1:3000/callback"
                },

                AllowedScopes =
                {
                    IdentityServerConstants.StandardScopes.OpenId,
                    IdentityServerConstants.StandardScopes.Profile,
                    IdentityServerConstants.StandardScopes.Email,
                    "roles",
                    MicroserviceApiResources.CUSTOMER_ONBOARDING_READ,
                    MicroserviceApiResources.CUSTOMER_ONBOARDING_WRITE,

                    MicroserviceApiResources.CUSTOMER_KYC_READ,
                    MicroserviceApiResources.CUSTOMER_KYC_WRITE,

                    MicroserviceApiResources.ACCOUNTS_READ,
                    MicroserviceApiResources.ACCOUNTS_WRITE,

                    MicroserviceApiResources.PAYMENTS_READ,
                    MicroserviceApiResources.PAYMENTS_WRITE
                },

                // Bruno doesn't need refresh-token support for our API testing client.
                AllowOfflineAccess = false,

                // Keep this true initially so we can explicitly see the consent step
                // while validating the OAuth configuration.
                RequireConsent = false
            },


            // Shell BFF Client (BFF using ASP.NET Core 10)
            new()
            {
                ClientId = BSSShellBFF.CLIENT_ID_FOR_IDP,
                ClientName = BSSShellBFF.CLIENT_NAME_FOR_IDP,
                ClientSecrets = { new Secret(BSSShellBFF.CLIENT_SECRET_FOR_IDP.Sha256()) },

                AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : The Authorization Code flow is the recommended OIDC flow for confidential server-side clients (BFFs).
                    //              It returns an authorization code to the server (via a browser redirection), which the server exchanges for tokens using its client secret.
                    //              This keeps access/refresh tokens off the browser and leverages server-side confidentiality.
                    // 🡡__ IF NOT: Using implicit or hybrid flows (or client-side flows) would expose tokens to the browser, increasing XSS risk.
                    //              If a non-confidential grant (e.g., Resource Owner Password) were used, it would require sending user credentials
                    //              to the client and reduce overall security. The client might also be unable to validate tokens or perform
                    //              secure token exchange in a standard way.
                RequirePkce = true,


                RedirectUris = { $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL }/signin-oidc" },
				PostLogoutRedirectUris = { $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
				FrontChannelLogoutUri = $"{BSSShellBFF.SHELL_BFF_CLIENT_BASE_URL }/signout-oidc",

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

                RefreshTokenUsage = TokenUsage.ReUse,
                    // 🡡__ WHY   : ReUse leaves the same refresh token valid for multiple refresh operations until it expires. This reduces storage
                    //              churn on the server and is simpler to implement. It is acceptable for many server-side clients where refresh tokens
                    //              are kept securely.
                    // 🡡__ IF NOT: If you choose TokenUsage.OneTimeOnly, the server issues a new refresh token each time the client uses the old one
                    //              (rotation). Rotation is more secure because stolen refresh tokens are invalidated after use, but it requires
                    //              tracking token rotation state and can increase complexity (and race-condition handling) on the server.


                RefreshTokenExpiration = TokenExpiration.Sliding,
                SlidingRefreshTokenLifetime = 3600
                    // 🡡__ WHY   : Sliding expiration extends the refresh token lifetime (by the configured window) each time it is used, enabling
                    //              long-lived sessions for active users while expiring tokens for inactive accounts. The Sliding lifetime here (3600)
                    //              is the renewal window (in seconds) applied on each use; adjust based on desired session duration and security posture.
                    // 🡡__ IF NOT: If you use Absolute expiration instead, refresh tokens will expire after a fixed period regardless of usage,
                    //              forcing reauthentication after that period. If Sliding is misconfigured (too long) you could unintentionally enable
                    //              excessively long sessions; if too short, users will be asked to re-login frequently.

            },

            // Customer Onboarding Microservice Client (BFF using ASP.NET Core 10)
            new()
            {
                ClientId = CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP,
                ClientName = CustomerOnboardingMicroservice.CLIENT_NAME_FOR_IDP,
                ClientSecrets = { new Secret(CustomerOnboardingMicroservice.CLIENT_SECRET_FOR_IDP.Sha256()) },

                AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : The CustomerOnboarding microservice (if acting as a confidential client or BFF) should use Authorization Code to keep tokens
                    //              private on the server and to benefit from the standard OIDC/OAuth flow, including PKCE if applicable.
                    // 🡡__ IF NOT: Using non-confidential or browser flows could expose tokens to the client-side, allowing token theft via XSS
                    //              and making secure API access more difficult to enforce.

                RequirePkce = true,

                RedirectUris = { $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL }/signin-oidc" },
                PostLogoutRedirectUris = { $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
                FrontChannelLogoutUri = $"{CustomerOnboardingMicroservice.BFF_CLIENT_BASE_URL }/signout-oidc",

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
                    MicroserviceApiResources.CUSTOMER_ONBOARDING_API
                        // 🡡__ WHY   : Including the CUSTOMER_ONBOARDING_API scope permits the CustomerOnboarding Microservice BFF client to request access tokens that include scope permissions for the
                        //              CustomerOnboarding Microservice API. The CustomerOnboarding Microservice API will validate the access token and require the corresponding scope to authorize API calls.
                        // 🡡__ IF NOT: If this scope is not included, tokens issued to the client will not be valid for calling the CustomerOnboarding Microservice API, so CustomerOnboarding Microservice API calls
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
            },

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
				FrontChannelLogoutUri = $"{CustomerKycMicroservice.BFF_CLIENT_BASE_URL }/signout-oidc",

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
					MicroserviceApiResources.CUSTOMER_KYC_API
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
            },

            // Accounts Microservice Client (BFF using NestJS, and not ASP.NET Core 10).
            new()
			{
				ClientId = AccountsMicroservice.CLIENT_ID_FOR_IDP,
				ClientName = AccountsMicroservice.CLIENT_NAME_FOR_IDP,
				ClientSecrets = { new Secret(AccountsMicroservice.CLIENT_SECRET_FOR_IDP.Sha256()) },

				AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : The Accounts microservice (if acting as a confidential client or BFF) should use Authorization Code to keep tokens
                    //              private on the server and to benefit from the standard OIDC/OAuth flow, including PKCE if applicable.
                    // 🡡__ IF NOT: Using non-confidential or browser flows could expose tokens to the client-side, allowing token theft via XSS
                    //              and making secure API access more difficult to enforce.

                RequirePkce = true,

                RedirectUris = { $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/api/auth/callback" },
				PostLogoutRedirectUris = { $"{AccountsMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
				FrontChannelLogoutUri = $"{AccountsMicroservice.BFF_CLIENT_BASE_URL }/signout-oidc",

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
					MicroserviceApiResources.ACCOUNTS_API
                        // 🡡__ WHY   : Including the ACCOUNTS_API scope permits the Accounts Microservice BFF client to request access tokens that include scope permissions for the
                        //              Accounts Microservice API. The Accounts Microservice API will validate the access token and require the corresponding scope to authorize API calls.
                        // 🡡__ IF NOT: If this scope is not included, tokens issued to the client will not be valid for calling the Accounts Microservice API, so Accounts Microservice API calls
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
            },
            // Payments Microservice Client (BFF using NestJS, and not ASP.NET Core 10).
            new()
			{
				ClientId = PaymentsMicroservice.CLIENT_ID_FOR_IDP,
				ClientName = PaymentsMicroservice.CLIENT_NAME_FOR_IDP,
				ClientSecrets = { new Secret(PaymentsMicroservice.CLIENT_SECRET_FOR_IDP.Sha256()) },

				AllowedGrantTypes = GrantTypes.Code,
                    // 🡡__ WHY   : The Payments microservice (if acting as a confidential client or BFF) should use Authorization Code to keep tokens
                    //              private on the server and to benefit from the standard OIDC/OAuth flow, including PKCE if applicable.
                    // 🡡__ IF NOT: Using non-confidential or browser flows could expose tokens to the client-side, allowing token theft via XSS
                    //              and making secure API access more difficult to enforce.

                RequirePkce = true,

                RedirectUris = { $"{PaymentsMicroservice.BFF_CLIENT_BASE_URL}/api/auth/callback" },
				PostLogoutRedirectUris = { $"{PaymentsMicroservice.BFF_CLIENT_BASE_URL}/signout-callback-oidc" },
				FrontChannelLogoutUri = $"{PaymentsMicroservice.BFF_CLIENT_BASE_URL }/signout-oidc",

				AllowOfflineAccess = true,
                    // 🡡__ WHY   : Payments Microservice BFF frontend may need refresh tokens to maintain backend sessions or to act on behalf of the user without interactive login.
                    //              For server-to-server or long-running operations, refresh tokens enable seamless token renewal.
                    // 🡡__ IF NOT: Without offline access, the service cannot obtain refresh tokens and must force users to re-authenticate when access tokens expire.

                AllowedScopes =
				{
					IdentityServerConstants.StandardScopes.OpenId,
					IdentityServerConstants.StandardScopes.Profile,
					IdentityServerConstants.StandardScopes.Email,
					"roles",
					MicroserviceApiResources.PAYMENTS_API
                        // 🡡__ WHY   : Including the PAYMENTS_API scope permits the Payments Microservice BFF client to request access tokens that include scope permissions for the
                        //              Payments Microservice API. The Payments Microservice API will validate the access token and require the corresponding scope to authorize API calls.
                        // 🡡__ IF NOT: If this scope is not included, tokens issued to the client will not be valid for calling the Payments Microservice API, so Payments Microservice API calls
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
            }
		];
}