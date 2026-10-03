using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients;

public sealed class Bruno
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return new()
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
                // Local Bruno callback server (npx @usebruno/oauth2-callback-server --port 3000);
                // the reliable option during V3 testing. See ReadMe.txt section 6.
                "http://127.0.0.1:3000/callback",
                "https://oauth.usebruno.com/callback"
            },

            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                IdentityServerConstants.StandardScopes.Email,
                "roles",
                CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ,
                CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE,

                CustomerKycApiScopesRequired.CUSTOMER_KYC_READ,
                CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE,

                DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_READ,
                DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE,

                AccountsApiScopesRequired.ACCOUNTS_READ,
                AccountsApiScopesRequired.ACCOUNTS_WRITE,

                PaymentsApiScopesRequired.PAYMENTS_READ,
                PaymentsApiScopesRequired.PAYMENTS_WRITE
            },

                // Bruno doesn't need refresh-token support for our API testing client.
                AllowOfflineAccess = false,

                // Keep this true initially so we can explicitly see the consent step
                // while validating the OAuth configuration.
                RequireConsent = false
            };
        }
    }
}