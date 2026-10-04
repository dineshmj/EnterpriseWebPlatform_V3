using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the ComplianceCaseOpeningSubscriber: it may only open
/// compliance cases (compliance.write), and the Compliance API pins this client
/// ID on its internal endpoint.
/// </summary>
public sealed class ComplianceCaseOpeningSubscriberToComplianceApiM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = ComplianceMicroservice.CLIENT_ID_FOR_IDP_FOR_COMPLIANCE_CASE_OPENING_SUBSCRIBER_TO_COMPLIANCE_API_M2M,
            ClientName = ComplianceMicroservice.CLIENT_NAME_FOR_IDP_FOR_COMPLIANCE_CASE_OPENING_SUBSCRIBER_TO_COMPLIANCE_API_M2M,
            ClientSecrets = { ClientSecretStore.For(ComplianceMicroservice.CLIENT_ID_FOR_IDP_FOR_COMPLIANCE_CASE_OPENING_SUBSCRIBER_TO_COMPLIANCE_API_M2M) },
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { ComplianceApiScopesRequired.COMPLIANCE_WRITE }
        };
}