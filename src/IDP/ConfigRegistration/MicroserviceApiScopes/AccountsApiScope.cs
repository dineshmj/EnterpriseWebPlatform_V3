using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class AccountsApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                AccountsApiScopesRequired.ACCOUNTS_READ,
                "Accounts API - Read")
        {
            UserClaims = { "role", "name", "email" }
        };

    public static ApiScope Write =>
        new(
                AccountsApiScopesRequired.ACCOUNTS_WRITE,
                "Accounts API - Write")
        {
            UserClaims = { "role", "name", "email" }
        };
}