using EnterpriseWebPlatform.IdentityServer.Security;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

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

                    AllowedGrantTypes = GrantTypes.ClientCredentials,
                    // 🡡__ WHY   : The subscriber is a machine identity that delivers KYC workflow facts to the Customer
                    //              Onboarding API; there is no interactive user session.
                    // 🡡__ IF NOT: Authorization Code would require a browser and a user, which a worker does not have.

                    AllowedScopes =
                    {
                        CustomerOnboardingApiScopesRequired.CUSTOMER_ONBOARDING_WRITE
                            // 🡡__ WHY   : Least privilege - the subscriber only records KYC outcomes. The Customer
                            //              Onboarding API additionally pins this client_id on its internal endpoint, so the
                            //              scope alone grants nothing else.
                            // 🡡__ IF NOT: Without it the IDP cannot issue a token the Customer Onboarding API accepts.
                    }
                };
        }
    }
}
