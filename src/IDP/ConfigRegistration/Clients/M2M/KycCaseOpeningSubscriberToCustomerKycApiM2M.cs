using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

public sealed class KycCaseOpeningSubscriberToCustomerKycApiM2M
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Customer KYC Subscribre M2M Client
                new()
                {
                    ClientId = ClientSettings.Ids.KycCaseOpeningSubscriber,
                    ClientName = "KYC Case Opening Subscriber to Customer KYC API M2M Client",
                    ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.KycCaseOpeningSubscriber) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Client Credentials: a machine identity with no person and no browser - one client per
                    // │ caller-callee purpose.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = GrantTypes.ClientCredentials,

                    AllowedScopes =
                    {
                        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                        // │ Least privilege: only the write scope this worker needs; the KYC API also pins this
                        // │ client_id on its internal endpoint.
                        // └──────────────────────────────────────────────────────────────────────────────────────
                        "customer-kyc.write"
                
                    }
                };
        }
    }
}