using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
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
                    ClientId = CustomerKycMicroservice.CLIENT_ID_FOR_IDP_FOR_KYC_CASE_OPENING_SUBSCRIBER_TO_CUST_KYC_API_M2M,
                    ClientName = CustomerKycMicroservice.CLIENT_NAME_FOR_IDP_FOR_KYC_CASE_OPENING_SUBSCRIBER_TO_CUST_KYC_API_M2M,
                    ClientSecrets = { ClientSecretStore.For(CustomerKycMicroservice.CLIENT_ID_FOR_IDP_FOR_KYC_CASE_OPENING_SUBSCRIBER_TO_CUST_KYC_API_M2M) },

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
                        CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE
                
                    }
                };
        }
    }
}