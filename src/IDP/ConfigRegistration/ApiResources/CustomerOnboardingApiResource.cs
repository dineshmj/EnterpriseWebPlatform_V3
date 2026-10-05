using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class CustomerOnboardingApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
            MicroserviceApiResourceNames.CUSTOMER_ONBOARDING_API,
            "Customer Onboarding API")
        {
            Scopes =
                {
                    CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ,
                    CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE
                },

            UserClaims =
                {
                    "role",
                    "name",
                    "email",

                    // The acting staff member's LAN ID, recorded next to the subject ID
                    "lan_id",

                    // ABAC: branch scope of a Customer Service Agent
                    "branch",
                    "branch_city",
                    "branch_country_code"
                }
        };
}