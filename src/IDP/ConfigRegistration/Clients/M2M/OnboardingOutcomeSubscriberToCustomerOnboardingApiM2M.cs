using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

public sealed class OnboardingOutcomeSubscriberToCustomerOnboardingApiM2M
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Onboarding Outcome Subscriber M2M Client
                new()
                {
                    ClientId = CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP_FOR_ONBOARDING_OUTCOME_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M,
                    ClientName = CustomerOnboardingMicroservice.CLIENT_NAME_FOR_IDP_FOR_ONBOARDING_OUTCOME_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M,
                    ClientSecrets = { ClientSecretStore.For(CustomerOnboardingMicroservice.CLIENT_ID_FOR_IDP_FOR_ONBOARDING_OUTCOME_SUBSCRIBER_TO_CUST_ONBOARDING_API_M2M) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Client Credentials: a machine identity with no person and no browser - one client per
                    // │ caller-callee purpose.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = GrantTypes.ClientCredentials,

                    AllowedScopes =
                    {
                        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                        // │ Least privilege: only the write scope this worker needs; the Customer Onboarding API
                        // │ also pins this client_id on its internal endpoint.
                        // └──────────────────────────────────────────────────────────────────────────────────────
                        CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE
                    }
                };
        }
    }
}