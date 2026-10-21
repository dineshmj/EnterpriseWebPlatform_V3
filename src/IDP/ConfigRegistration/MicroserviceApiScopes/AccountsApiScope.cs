using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class AccountsApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                "accounts.read",
                "Accounts API - Read")
        {
            UserClaims = { "role", "name", "email" }
        };

    public static ApiScope Write =>
        new(
                "accounts.write",
                "Accounts API - Write")
        {
            UserClaims = { "role", "name", "email" }
        };
}