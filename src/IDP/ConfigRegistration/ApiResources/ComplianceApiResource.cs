using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class ComplianceApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "compliance-api",
                "Compliance API")
        {
            Scopes =
                {
                    "compliance.read",
                    "compliance.write"
                },

            // The ABAC attributes the Compliance API authorizes on (clearance by risk, branch scope).
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