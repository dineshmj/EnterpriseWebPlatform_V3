using Duende.IdentityServer.Models;


namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.ApiResources;

public sealed class DocumentsManagementApiResource : IDuendeApiResource
{
    public static ApiResource ApiResource =>
        new(
                "documents-management-api",
                "Documents Management API")
        {
            Scopes =
                {
                    "documents-management.read",
                    "documents-management.write"
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