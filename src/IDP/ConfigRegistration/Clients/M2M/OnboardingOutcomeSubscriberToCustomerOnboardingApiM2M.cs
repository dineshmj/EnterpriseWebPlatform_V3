using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
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
                    ClientId = ClientSettings.Ids.OnboardingOutcomeSubscriber,
                    ClientName = "Onboarding Outcome Subscriber to Customer Onboarding API M2M Client",
                    ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.OnboardingOutcomeSubscriber) },

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
                        "customer-onboarding.write"
                    }
                };
        }
    }
}