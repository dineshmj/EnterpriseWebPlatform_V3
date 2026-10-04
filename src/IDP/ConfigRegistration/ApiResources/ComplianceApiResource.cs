using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class ComplianceApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.COMPLIANCE_API,
                "Compliance API")
        {
            Scopes =
                {
                    ComplianceApiScopesRequired.COMPLIANCE_READ,
                    ComplianceApiScopesRequired.COMPLIANCE_WRITE
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
                    "employment_type"
                }
        };
}