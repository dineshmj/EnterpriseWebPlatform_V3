using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class PaymentsApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "payments-api",
                "Payments API")
        {
            Scopes =
                {
                    "payments.read",
                    "payments.write"
                },

            // The ABAC attributes the Payments API authorizes on (permission, branch scope;
            // department and clearance for the approval tiers in 5b), and the LAN ID it shows.
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