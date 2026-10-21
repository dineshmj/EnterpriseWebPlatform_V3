using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class CustomerKycApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                "customer-kyc.read",
                "Customer KYC API - Read")
        {
            UserClaims = { "role", "name", "email", "permission", "department", "branch", "region", "clearance_level", "employment_type" }
        };

    public static ApiScope Write =>
        new(
                "customer-kyc.write",
                "Customer KYC API - Write")
        {
            UserClaims = { "role", "name", "email", "permission", "department", "branch", "region", "clearance_level", "employment_type" }
        };
}