namespace EnterpriseWebPlatform.Common.Landscape.Microservices.ApiScopes;

public static class NotificationsApiScopesRequired
{
    /// <summary>Read one's own notifications and receive them live (SignalR hub).</summary>
    public const string NOTIFICATIONS_READ =
        "notifications.read";

    /// <summary>Mark one's own notifications read; the subscriber's M2M identity publishes with it.</summary>
    public const string NOTIFICATIONS_WRITE =
        "notifications.write";
}