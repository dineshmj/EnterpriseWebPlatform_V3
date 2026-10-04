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

                    AllowedGrantTypes = GrantTypes.ClientCredentials,
                    // 🡡__ WHY   : The KYC Case Opening Subscriber M2M client is a machine identity and does not represent an
                    //              interactive user session. Client Credentials allows the client to authenticate itself
                    //              using its client credentials and obtain an access token for the protected API.
                    // 🡡__ IF NOT: Using Authorization Code would introduce browser-based authentication, redirects and
                    //              user interaction that are not applicable to a machine-to-machine client.

                    AllowedScopes =
                    {
                        CustomerKycApiScopesRequired.CUSTOMER_KYC_WRITE
                            // 🡡__ WHY   : The M2M client only needs write access to the Customer KYC API for the
                            //              operations it is trusted to perform. This follows the least-privilege principle.
                            // 🡡__ IF NOT: Without this scope, the IDP will not issue an access token containing the required
                            //              permission, and the Customer KYC API will reject protected write operations.
                
                    }
                };
        }
    }
}