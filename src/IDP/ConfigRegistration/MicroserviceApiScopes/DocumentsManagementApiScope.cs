using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class DocumentsManagementApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_READ,
                "Documents Management API - Read")
        {
            UserClaims = { "role", "name", "email" }
        };

    public static ApiScope Write =>
        new(
                DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE,
                "Documents Management API - Write")
        {
            UserClaims = { "role", "name", "email" }
        };
}