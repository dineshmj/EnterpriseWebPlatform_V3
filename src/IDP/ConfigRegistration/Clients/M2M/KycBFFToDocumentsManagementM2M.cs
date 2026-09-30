using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

public sealed class KycBFFToDocumentsManagementM2M
    : IDuendeClient
{
    public static Client Client
    {
        get
        {
            return
                // Documents Management M2M Client
                new()
                {
                    ClientId = DocumentsManagementMicroservice.CLIENT_ID_FOR_IDP_FOR_KYC_BFF_TO_DOC_MGMT_M2M,
                    ClientName = DocumentsManagementMicroservice.CLIENT_NAME_FOR_IDP_FOR_KYC_BFF_TO_DOC_MGMT_M2M,
                    ClientSecrets = { new Secret(DocumentsManagementMicroservice.CLIENT_SECRET_FOR_IDP_FOR_KYC_BFF_TO_DOC_MGMT_M2M.Sha256()) },

                    AllowedGrantTypes = GrantTypes.ClientCredentials,
                    // 🡡__ WHY   : The Documents Management M2M client is a machine identity and does not represent an
                    //              interactive user session. Client Credentials allows the client to authenticate itself
                    //              using its client credentials and obtain an access token for the protected API.
                    // 🡡__ IF NOT: Using Authorization Code would introduce browser-based authentication, redirects and
                    //              user interaction that are not applicable to a machine-to-machine client.

                    AllowedScopes =
                    {
                        DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_READ
                            // 🡡__ WHY   : The M2M client only needs read access to the Documents Management API for the
                            //              operations it is trusted to perform. This follows the least-privilege principle.
                            // 🡡__ IF NOT: Without this scope, the IDP will not issue an access token containing the required
                            //              permission, and the Documents Management API will reject protected read operations.
                
                    }
                };
        }
    }
}