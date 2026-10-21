using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the AccountsCommandSubscriber: it may only hand funds commands (from
/// the Payments saga orchestrator) to the Accounts API (accounts.write), which pins this
/// client ID on its internal endpoint.
/// </summary>
public sealed class AccountsCommandSubscriberToAccountsApiM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = ClientSettings.Ids.AccountsCommandSubscriber,
            ClientName = "Accounts Command Subscriber to Accounts API M2M Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.AccountsCommandSubscriber) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Client Credentials with ONE write scope: a machine identity for one caller-callee
            // │ purpose; the receiving API also pins this client_id on its internal endpoint.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { "accounts.write" }
        };
}