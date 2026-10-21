using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
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
            ClientId = ClientSettings.Ids.PartnerProductsWorker,
            ClientName = "Partner Products Worker (Temporal) M2M Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.PartnerProductsWorker) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Client credentials, no person: the worker never acts FOR a staff member (it is
            // │ not on the token-exchange allow-list). Two scopes for its one purpose; the scopes
            // │ alone open nothing - only the internal endpoints that pin this client ID accept it
            // │ (every staff endpoint also needs a person's permission and branch).
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { "payments.read", "partner-products.write" },
            AccessTokenLifetime = 300
        };
}