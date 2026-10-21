using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class AccountsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "accounts-api",
                "Accounts API")
        {
            Scopes =
                {
                    "accounts.read",
                    "accounts.write"
                },

            // The ABAC attributes the Accounts API authorizes on (branch scope, officer department, clearance).
            UserClaims =
                {
                    "role",
                    "name",
                    "email",
                    "permission",
                    "department",
                    "branch",
                    "region",
                    "clearance_level",
                    "lan_id",
                    "employment_type"
                }
        };
}