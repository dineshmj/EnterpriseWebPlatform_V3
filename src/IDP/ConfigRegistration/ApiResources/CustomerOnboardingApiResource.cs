using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class CustomerOnboardingApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
            "customer-onboarding-api",
            "Customer Onboarding API")
        {
            Scopes =
                {
                    "customer-onboarding.read",
                    "customer-onboarding.write",
                    // The IDP itself during a customer's online registration (no person in the token).
                    "customer-onboarding.online-identity"
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