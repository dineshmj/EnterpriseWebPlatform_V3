using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using EnterpriseWebPlatform.Notifications.Api.Application;
using EnterpriseWebPlatform.Notifications.Api.Domain;

namespace EnterpriseWebPlatform.Notifications.Api.Hubs;

// ┌─ :: ARCHITECTURE + SECURITY :: Real-time push over SignalR ────────────────────────────────────
// │ Why : A WebSocket kept open forever outlives the sign-in, and one person opening hundreds of
// │       connections can exhaust the server.
// │ How : Only signed-in people (scope notifications.read) connect; each joins only the groups of
// │       its own audiences; connections per person are limited; the connection closes when the
// │       token expires.
// │ Ref : Features 1.5.4, 2.4.7 | OWASP API4:2023 (resource consumption) | ASVS V3
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// The live channel. The Shell BFF proxies one connection per browser here and adds the
/// person's access token; the hub joins the connection to the person's audiences, taken
/// from that token only. Clients cannot join or leave groups: the hub exposes no methods
/// to them - it only sends "notification". Each person may hold a limited number of
/// connections at once (<see cref="HubConnectionLimiter"/>): one more is closed at once.
/// </summary>
[Authorize(Policy = "NotificationsRead")]
public sealed class NotificationsHub(HubConnectionLimiter limiter, ILogger<NotificationsHub> logger) : Hub
{
    public const string Path = "/hubs/notifications";
    public const string NotificationMethod = "notification";
    private const string CountedKey = "ewp:counted-for";

    public override async Task OnConnectedAsync()
    {
        var person = Context.User?.FindFirst("sub")?.Value;
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If the person already holds the maximum number of connections (or has no sub), refuse
        // │ the new one.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (person is null || !limiter.TryAcquire(person))
        {
            logger.LogWarning("Notifications connection {ConnectionId} refused: {Reason}.", Context.ConnectionId,
                person is null ? "no subject in the token" : $"the person already holds {limiter.MaxPerPerson} connections");
            Context.Abort();
            return;
        }
        Context.Items[CountedKey] = person;

        var audiences = NotificationAudiences.Of(Context.User!);
        foreach (var audience in audiences)
            await Groups.AddToGroupAsync(Context.ConnectionId, audience, Context.ConnectionAborted);

        logger.LogInformation("Notifications connection {ConnectionId} joined {Audiences}.", Context.ConnectionId, string.Join(", ", audiences));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(CountedKey, out var person) && person is string sub)
            limiter.Release(sub);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// At most "Notifications:MaxConnectionsPerPerson" (default 10: a few browser tabs) live
/// connections per person on this instance, so a stolen session or a faulty client cannot hold
/// thousands of sockets open. Several instances would each apply the limit (a shared count
/// needs the backplane's store).
/// </summary>
public sealed class HubConnectionLimiter(int maxPerPerson)
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public int MaxPerPerson { get; } = maxPerPerson > 0 ? maxPerPerson
        : throw new InvalidOperationException("Notifications:MaxConnectionsPerPerson must be positive.");

    public bool TryAcquire(string person)
    {
        lock (_gate)
        {
            var count = _counts.GetValueOrDefault(person);
            if (count >= MaxPerPerson)
                return false;
            _counts[person] = count + 1;
            return true;
        }
    }

    public void Release(string person)
    {
        lock (_gate)
        {
            if (!_counts.TryGetValue(person, out var count))
                return;
            if (count <= 1) _counts.Remove(person);
            else _counts[person] = count - 1;
        }
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