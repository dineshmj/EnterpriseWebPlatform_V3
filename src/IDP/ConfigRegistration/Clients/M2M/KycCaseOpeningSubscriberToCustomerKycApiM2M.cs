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
                    ClientSecrets = { ClientKeyStore.For(ClientSettings.Ids.KycCaseOpeningSubscriber) },

                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ Client Credentials: a machine identity with no person and no browser - one client per
                    // │ caller-callee purpose.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    AllowedGrantTypes = GrantTypes.ClientCredentials,
                    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
                    // │ private_key_jwt and DPoP (FAPI 2.0): the worker proves itself with a one-time
                    // │ assertion signed by its own key (the IDP holds only the public key - no shared
                    // │ secret), and every token is bound to its DPoP key (cnf.jkt), so a token copied
                    // │ from a log or a proxy cannot be used by anyone else.
                    // └──────────────────────────────────────────────────────────────────────────────────────
                    RequireDPoP = true,

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