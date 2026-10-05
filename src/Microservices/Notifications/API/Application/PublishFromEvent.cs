using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using EnterpriseWebPlatform.Notifications.Api.Domain;
using EnterpriseWebPlatform.Notifications.Api.Infrastructure;

namespace EnterpriseWebPlatform.Notifications.Api.Application;

public enum PublishOutcome
{
    Published,
    NothingToTell,
    Duplicate
}

/// <summary>The live channel (SignalR). Best effort: a stored notification is never lost if the push fails.</summary>
public interface INotificationPusher
{
    Task PushAsync(IReadOnlyList<Notification> notifications, CancellationToken cancellationToken);
}

/// <summary>
/// Turns one workflow event into notifications: Inbox check, rules, store - in one
/// transaction - then push live. Stored first, pushed second: a person who is offline,
/// or whose connection drops, finds the notification in the bell later.
/// </summary>
public sealed class PublishFromEventHandler(
    NotificationsDbContext db,
    INotificationPusher pusher,
    TimeProvider clock,
    ILogger<PublishFromEventHandler> logger)
{
    public const string InboxConsumer = "notifications.publisher";

    public async Task<(PublishOutcome Outcome, int Count)> HandleAsync(
        Guid messageId, string eventType, JsonElement envelope, CancellationToken cancellationToken)
    {
        if (await db.InboxMessages.AnyAsync(x => x.MessageId == messageId && x.Consumer == InboxConsumer, cancellationToken))
            return (PublishOutcome.Duplicate, 0);

        var now = clock.GetUtcNow();
        var notifications = NotificationRules.For(eventType, envelope)
            .Select(draft => Notification.Create(draft, messageId, eventType, now))
            .ToList();

        // Recorded even when nobody is told, so the event is never evaluated twice.
        db.Notifications.AddRange(notifications);
        db.InboxMessages.Add(InboxMessage.Processed(messageId, InboxConsumer, now));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two deliveries of the same event raced; the unique keys let exactly one commit.
            return (PublishOutcome.Duplicate, 0);
        }

        if (notifications.Count == 0)
            return (PublishOutcome.NothingToTell, 0);

        try
        {
            await pusher.PushAsync(notifications, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Stored {Count} notification(s) for {EventType} {MessageId} but the live push failed; they are delivered on the next load.",
                notifications.Count, eventType, messageId);
        }

        return (PublishOutcome.Published, notifications.Count);
    }
}