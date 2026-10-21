using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class ComplianceApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                "compliance.read",
                "Compliance API - Read")
        {
            UserClaims = { "role", "name", "email", "permission", "department", "branch", "region", "clearance_level", "employment_type" }
        };

    public static ApiScope Write =>
        new(
                "compliance.write",
                "Compliance API - Write")
        {
            UserClaims = { "role", "name", "email", "permission", "department", "branch", "region", "clearance_level", "employment_type" }
        };
}