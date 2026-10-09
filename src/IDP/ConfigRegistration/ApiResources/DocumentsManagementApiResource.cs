using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices;
using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class DocumentsManagementApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                MicroserviceApiResourceNames.DOCUMENTS_MANAGEMENT_API,
                "Documents Management API")
        {
            Scopes =
                {
                    DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_READ,
                    DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE
                },

            UserClaims =
                {
                    "role",
                    "name",
                    "email",
                    // The acting person's branch (token exchange): documents are branch-scoped.
                    "branch"
                }
        };
}