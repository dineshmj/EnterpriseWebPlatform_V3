using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the AccountApplicationOpeningSubscriber: it may only open
/// account applications (accounts.write), and the Accounts API pins this client
/// ID on its internal endpoint.
/// </summary>
public sealed class AccountApplicationOpeningSubscriberToAccountsApiM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = ClientSettings.Ids.AccountApplicationOpeningSubscriber,
            ClientName = "Account Application Opening Subscriber to Accounts API M2M Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.AccountApplicationOpeningSubscriber) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Client Credentials with ONE write scope: a machine identity for one caller-callee
            // │ purpose; the receiving API also pins this client_id on its internal endpoint.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { "accounts.write" }
        };
}