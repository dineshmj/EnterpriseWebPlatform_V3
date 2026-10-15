using Duende.IdentityModel;
using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

public sealed class OperationsJourneyApiTokenExchange
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = OperationsMicroservice.CLIENT_ID_FOR_IDP_FOR_OPERATIONS_JOURNEY_API,
            ClientName = OperationsMicroservice.CLIENT_NAME_FOR_IDP_FOR_OPERATIONS_JOURNEY_API,
            ClientSecrets = { ClientSecretStore.For(OperationsMicroservice.CLIENT_ID_FOR_IDP_FOR_OPERATIONS_JOURNEY_API) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Token exchange ONLY - no client credentials: the Operations Journey API can never call
            // │ Accounts or Compliance as itself, only for the signed-in operator - whose own
            // │ permissions (outbox.view, workflow.reprocess) and MFA freshness travel with the token.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = { OidcConstants.GrantTypes.TokenExchange },
            AllowedScopes =
            {
                AccountsApiScopesRequired.ACCOUNTS_READ, AccountsApiScopesRequired.ACCOUNTS_WRITE,
                ComplianceApiScopesRequired.COMPLIANCE_READ, ComplianceApiScopesRequired.COMPLIANCE_WRITE
            },

            // Delegated tokens are short-lived: one request, one call.
            AccessTokenLifetime = 300
        };
}