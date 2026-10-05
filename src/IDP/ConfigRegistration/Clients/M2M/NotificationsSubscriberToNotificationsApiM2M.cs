using Duende.IdentityServer.Models;

using EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;
using EnterpriseWebPlatform.Common.Landscape.Microservices.IdpInfo;
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
            ClientId = NotificationsMicroservice.CLIENT_ID_FOR_IDP_FOR_NOTIFICATIONS_SUBSCRIBER_TO_NOTIFICATIONS_API_M2M,
            ClientName = NotificationsMicroservice.CLIENT_NAME_FOR_IDP_FOR_NOTIFICATIONS_SUBSCRIBER_TO_NOTIFICATIONS_API_M2M,
            ClientSecrets = { ClientSecretStore.For(NotificationsMicroservice.CLIENT_ID_FOR_IDP_FOR_NOTIFICATIONS_SUBSCRIBER_TO_NOTIFICATIONS_API_M2M) },
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { NotificationsApiScopesRequired.NOTIFICATIONS_WRITE }
        };
}