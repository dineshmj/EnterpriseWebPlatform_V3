using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class CustomerKycApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.CUSTOMER_KYC_API,
                "Customer KYC API")
        {
            Scopes =
                {
                    CustomerKycApiScopesRequired.CUSTOMER_KYC_READ,
                    CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE
                },

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