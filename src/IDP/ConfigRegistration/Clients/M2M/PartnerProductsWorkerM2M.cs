using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the Partner Products Worker (the Temporal purchase workflow): it reads a
/// payment's outcome (payments.read) and sends the Partner Bank's payment advice or cancellation
/// (partner-products.write). Both APIs pin this client ID on their internal endpoints.
/// </summary>
public sealed class PartnerProductsWorkerM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = PartnerProductsMicroservice.CLIENT_ID_FOR_IDP_FOR_PARTNER_PRODUCTS_WORKER_M2M,
            ClientName = PartnerProductsMicroservice.CLIENT_NAME_FOR_IDP_FOR_PARTNER_PRODUCTS_WORKER_M2M,
            ClientSecrets = { ClientSecretStore.For(PartnerProductsMicroservice.CLIENT_ID_FOR_IDP_FOR_PARTNER_PRODUCTS_WORKER_M2M) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Client credentials, no person: the worker never acts FOR a staff member (it is
            // │ not on the token-exchange allow-list). Two scopes for its one purpose; the scopes
            // │ alone open nothing - only the internal endpoints that pin this client ID accept it
            // │ (every staff endpoint also needs a person's permission and branch).
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { PaymentsApiScopesRequired.PAYMENTS_READ, PartnerProductsApiScopesRequired.PARTNER_PRODUCTS_WRITE },
            AccessTokenLifetime = 300
        };
}