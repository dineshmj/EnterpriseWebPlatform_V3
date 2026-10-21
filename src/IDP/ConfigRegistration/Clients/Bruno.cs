using Duende.IdentityServer;
using Duende.IdentityServer.Models;


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

                // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                // │ Public client for developer API testing (registered in Development only): no client
                // │ secret - it could not be kept on a workstation; Authorization Code + PKCE protects
                // │ the code instead.
                // └──────────────────────────────────────────────────────────────────────────────────────
                AllowedGrantTypes = GrantTypes.Code,

                RequirePkce = true,
                RequireClientSecret = false,

                RedirectUris =
            {
                // Local Bruno callback server (npx @usebruno/oauth2-callback-server --port 3000);
                // the reliable option during V3 testing. See ReadMe.txt section 11.
                "http://127.0.0.1:3000/callback",
                "https://oauth.usebruno.com/callback"
            },

            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                IdentityServerConstants.StandardScopes.Email,
                "roles",
                "customer-onboarding.read",
                "customer-onboarding.write",

                "customer-kyc.read",
                "customer-kyc.write",

                "documents-management.read",
                "documents-management.write",

                "compliance.read",
                "compliance.write",

                "accounts.read",
                "accounts.write",
                "notifications.read",
                "notifications.write",

                "payments.read",
                "payments.write"
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