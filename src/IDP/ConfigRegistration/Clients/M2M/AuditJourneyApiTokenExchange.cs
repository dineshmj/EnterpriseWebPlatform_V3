using Duende.IdentityModel;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// The Audit Journey API's machine identity. Its ONLY grant is token exchange: it can get a token
/// for the Audit API (and the Payments API, for a payment's current status) solely by presenting
/// a token the Audit web BFF obtained for a signed-in person. There is no client-credentials
/// grant, so it can never call a Domain API without a person behind it.
/// </summary>
public sealed class AuditJourneyApiTokenExchange
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = AuditMicroservice.CLIENT_ID_FOR_IDP_FOR_AUDIT_JOURNEY_API,
            ClientName = AuditMicroservice.CLIENT_NAME_FOR_IDP_FOR_AUDIT_JOURNEY_API,
            ClientSecrets = { ClientSecretStore.For(AuditMicroservice.CLIENT_ID_FOR_IDP_FOR_AUDIT_JOURNEY_API) },
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            AllowedScopes = { AuditApiScopesRequired.AUDIT_READ, PaymentsApiScopesRequired.PAYMENTS_READ },

            // Delegated tokens are short-lived: one journey, a few calls.
            AccessTokenLifetime = 300
        };
}