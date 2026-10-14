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

// ┌─ :: ARCHITECTURE :: Notifications from events, idempotently ───────────────────────────────────
// │ Why : Workflow events arrive at least once; a repeat must not notify twice, and a push failure
// │       must not lose the notification.
// │ How : The message ID goes to the Inbox in the same transaction as the notifications; the push
// │       happens after the commit and is best effort - the screen also reads the stored list.
// │ Ref : Features 1.3.2, 1.5.4 | Idempotent Consumer pattern
// └────────────────────────────────────────────────────────────────────────────────────────────────
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
        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ If this event was already turned into notifications, do nothing (Inbox).
        // └──────────────────────────────────────────────────────────────────────────────────────
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