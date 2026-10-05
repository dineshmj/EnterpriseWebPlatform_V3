using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using EnterpriseWebPlatform.Notifications.Api.Application;
using EnterpriseWebPlatform.Notifications.Api.Domain;

namespace EnterpriseWebPlatform.Notifications.Api.Hubs;

/// <summary>
/// The live channel. The Shell BFF proxies one connection per browser here and adds the
/// person's access token; the hub joins the connection to the person's audiences, taken
/// from that token only. Clients cannot join or leave groups: the hub exposes no methods
/// to them - it only sends "notification".
/// </summary>
[Authorize(Policy = "NotificationsRead")]
public sealed class NotificationsHub(ILogger<NotificationsHub> logger) : Hub
{
    public const string Path = "/hubs/notifications";
    public const string NotificationMethod = "notification";

    public override async Task OnConnectedAsync()
    {
        var audiences = NotificationAudiences.Of(Context.User!);
        foreach (var audience in audiences)
            await Groups.AddToGroupAsync(Context.ConnectionId, audience, Context.ConnectionAborted);

        logger.LogInformation("Notifications connection {ConnectionId} joined {Audiences}.", Context.ConnectionId, string.Join(", ", audiences));
        await base.OnConnectedAsync();
    }
}

/// <summary>Pushes stored notifications to the connections of their audience.</summary>
public sealed class SignalRNotificationPusher(IHubContext<NotificationsHub> hub) : INotificationPusher
{
    public async Task PushAsync(IReadOnlyList<Notification> notifications, CancellationToken cancellationToken)
    {
        foreach (var notification in notifications)
            await hub.Clients.Group(notification.Audience)
                .SendAsync(NotificationsHub.NotificationMethod, NotificationDto.From(notification, read: false), cancellationToken);
    }
}