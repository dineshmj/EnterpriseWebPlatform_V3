using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class CustomerKycApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                CustomerKycApiScopesRequired.CUSTOMER_KYC_READ,
                "Customer KYC API - Read")
        {
            UserClaims = { "role", "name", "email" }
        };

    public static ApiScope Write =>
        new(
                CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE,
                "Customer KYC API - Write")
        {
            UserClaims = { "role", "name", "email" }
        };
}