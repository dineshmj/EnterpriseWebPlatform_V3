using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class ComplianceApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                ComplianceApiScopesRequired.COMPLIANCE_READ,
                "Compliance API - Read")
        {
            UserClaims = { "role", "name", "email", "permission", "department", "branch", "region", "clearance_level", "employment_type" }
        };

    public static ApiScope Write =>
        new(
                ComplianceApiScopesRequired.COMPLIANCE_WRITE,
                "Compliance API - Write")
        {
            UserClaims = { "role", "name", "email", "permission", "department", "branch", "region", "clearance_level", "employment_type" }
        };
}