using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
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
            ClientId = DocumentsManagementMicroservice.CLIENT_ID_FOR_IDP_FOR_DOCUMENT_INVALIDATION_SUBSCRIBER_TO_DOC_MGMT_API_M2M,
            ClientName = DocumentsManagementMicroservice.CLIENT_NAME_FOR_IDP_FOR_DOCUMENT_INVALIDATION_SUBSCRIBER_TO_DOC_MGMT_API_M2M,
            ClientSecrets = { ClientSecretStore.For(DocumentsManagementMicroservice.CLIENT_ID_FOR_IDP_FOR_DOCUMENT_INVALIDATION_SUBSCRIBER_TO_DOC_MGMT_API_M2M) },
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { DocumentsManagementApiScopesRequired.DOCUMENTS_MANAGEMENT_WRITE }
        };
}