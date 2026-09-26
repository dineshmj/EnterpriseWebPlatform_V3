using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class CustomerOnboardingApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_READ,
            "Customer Onboarding API - Read")
            {
                UserClaims = { "role", "name", "email" }
            };

    public static ApiScope Write =>
        new(
            CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE,
            "Customer Onboarding API - Write")
            {
                UserClaims = { "role", "name", "email" }
            };
}