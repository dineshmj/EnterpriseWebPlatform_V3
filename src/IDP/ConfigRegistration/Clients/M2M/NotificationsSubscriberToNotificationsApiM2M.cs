using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;

/// <summary>
/// Machine identity of the NotificationsSubscriber: it may only hand workflow events to
/// the Notifications API (notifications.write), which pins this client ID on its
/// internal endpoint.
/// </summary>
public sealed class NotificationsSubscriberToNotificationsApiM2M
    : IDuendeClient
{
    public static Client Client =>
        new()
        {
            ClientId = ClientSettings.Ids.NotificationsSubscriber,
            ClientName = "Notifications Subscriber to Notifications API M2M Client",
            ClientSecrets = { ClientSecretStore.For(ClientSettings.Ids.NotificationsSubscriber) },
            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ Client Credentials with ONE write scope: a machine identity for one caller-callee
            // │ purpose; the receiving API also pins this client_id on its internal endpoint.
            // └──────────────────────────────────────────────────────────────────────────────────────
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { "notifications.write" }
        };
}