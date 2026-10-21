using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.MicroserviceApiScopes;

public sealed class DocumentsManagementApiScope : IDuendeMicroserviceApiScope
{
    public static ApiScope Read =>
        new(
                "documents-management.read",
                "Documents Management API - Read")
        {
            UserClaims = { "role", "name", "email" }
        };

    public static ApiScope Write =>
        new(
                "documents-management.write",
                "Documents Management API - Write")
        {
            UserClaims = { "role", "name", "email" }
        };
}