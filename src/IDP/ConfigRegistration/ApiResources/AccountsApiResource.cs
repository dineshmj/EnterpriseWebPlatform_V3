using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class AccountsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.ACCOUNTS_API,
                "Accounts API")
        {
            Scopes =
                {
                    AccountsApiScopesRequired.ACCOUNTS_READ,
                    AccountsApiScopesRequired.ACCOUNTS_WRITE
                },

            UserClaims =
                {
                    "role",
                    "name",
                    "email"
                }
        };
}