using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
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
            ClientId = ClientSettings.Ids.ComplianceCaseOpeningSubscriber,
            ClientName = "Compliance Case Opening Subscriber to Compliance API M2M Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.ComplianceCaseOpeningSubscriber) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Client Credentials with ONE write scope: a machine identity for one caller-callee
            // │ purpose; the receiving API also pins this client_id on its internal endpoint.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { "compliance.write" }
        };
}