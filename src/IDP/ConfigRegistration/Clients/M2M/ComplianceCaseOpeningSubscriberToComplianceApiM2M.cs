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
            ClientSecrets = { ClientKeyStore.For(ClientSettings.Ids.ComplianceCaseOpeningSubscriber) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Client Credentials with ONE write scope: a machine identity for one caller-callee
            // │ purpose; the receiving API also pins this client_id on its internal endpoint.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ private_key_jwt and DPoP (FAPI 2.0): the worker proves itself with a one-time
            // │ assertion signed by its own key (the IDP holds only the public key - no shared
            // │ secret), and every token is bound to its DPoP key (cnf.jkt), so a token copied
            // │ from a log or a proxy cannot be used by anyone else.
            // └──────────────────────────────────────────────────────────────────────────────────────
            RequireDPoP = true,
            AllowedScopes = { "compliance.write" }
        };
}