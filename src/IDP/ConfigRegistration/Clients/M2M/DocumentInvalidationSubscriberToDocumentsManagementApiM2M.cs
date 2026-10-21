using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the DocumentInvalidationSubscriber: it may only invalidate
/// documents (documents-management.write), and the Documents Management API pins this
/// client ID on its internal invalidation endpoint.
/// </summary>
public sealed class DocumentInvalidationSubscriberToDocumentsManagementApiM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = ClientSettings.Ids.DocumentInvalidationSubscriber,
            ClientName = "Document Invalidation Subscriber to Documents Management API M2M Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.DocumentInvalidationSubscriber) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Client Credentials with ONE write scope: a machine identity for one caller-callee
            // │ purpose; the receiving API also pins this client_id on its internal endpoint.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { "documents-management.write" }
        };
}