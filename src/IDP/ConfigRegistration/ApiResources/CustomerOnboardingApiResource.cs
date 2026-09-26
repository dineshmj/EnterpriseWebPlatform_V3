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
                    "email"
                }
        };
}