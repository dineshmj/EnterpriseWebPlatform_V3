using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class CustomerKycApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "customer-kyc-api",
                "Customer KYC API")
        {
            Scopes =
                {
                    "customer-kyc.read",
                    "customer-kyc.write"
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