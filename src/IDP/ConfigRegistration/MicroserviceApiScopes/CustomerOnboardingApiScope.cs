using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class CustomerOnboardingApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new("customer-onboarding.read",
            "Customer Onboarding API - Read")
            {
                UserClaims = { "role", "name", "email" }
            };

    public static ApiScope Write =>
        new(
            "customer-onboarding.write",
            "Customer Onboarding API - Write")
            {
                UserClaims = { "role", "name", "email" }
            };
}