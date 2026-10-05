using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Notifications.Api.Domain;
using EnterpriseWebPlatform.Notifications.Api.Infrastructure;

namespace EnterpriseWebPlatform.Notifications.Api.Application;

/// <summary>What the Shell shows: one notification, read or not for this person.</summary>
public sealed record NotificationDto(
    long Id,
    string Category,
    string Title,
    string Body,
    JsonElement? Target,
    DateTimeOffset CreatedAt,
    bool Read)
{
    public static NotificationDto From(Notification n, bool read) =>
        new(n.Id, n.Category, n.Title, n.Body, n.Target is null ? null : JsonDocument.Parse(n.Target).RootElement.Clone(), n.CreatedAt, read);
}

public sealed record NotificationsPage(IReadOnlyList<NotificationDto> Items, int UnreadCount);

/// <summary>
/// A person's notifications: those addressed to them and to their role in their branch
/// (audiences come from the token, never from the request). Marking read is per person.
/// </summary>
public sealed class NotificationQueries(NotificationsDbContext db, TimeProvider clock)
{
    public const int MaxItems = 50;

    public async Task<NotificationsPage> GetAsync(string userId, IReadOnlyList<string> audiences, bool unreadOnly, CancellationToken cancellationToken)
    {
        var visible = db.Notifications.AsNoTracking().Where(n => audiences.Contains(n.Audience));
        var unread = visible.Where(n => !db.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId));

        var unreadCount = await unread.CountAsync(cancellationToken);

        var rows = await (unreadOnly ? unread : visible)
            .OrderByDescending(n => n.Id)
            .Take(MaxItems)
            .Select(n => new { Notification = n, Read = db.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId) })
            .ToListAsync(cancellationToken);

        return new NotificationsPage([.. rows.Select(x => NotificationDto.From(x.Notification, x.Read))], unreadCount);
    }

    /// <summary>False when the notification does not exist or is not addressed to this person.</summary>
    public async Task<bool> MarkReadAsync(string userId, IReadOnlyList<string> audiences, long notificationId, CancellationToken cancellationToken)
    {
        var visible = await db.Notifications.AnyAsync(n => n.Id == notificationId && audiences.Contains(n.Audience), cancellationToken);
        if (!visible)
            return false;

        await InsertReadsAsync(userId, [notificationId], cancellationToken);
        return true;
    }

    public async Task<int> MarkAllReadAsync(string userId, IReadOnlyList<string> audiences, CancellationToken cancellationToken)
    {
        var ids = await db.Notifications.AsNoTracking()
            .Where(n => audiences.Contains(n.Audience) && !db.NotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId))
            .Select(n => n.Id)
            .ToListAsync(cancellationToken);

        await InsertReadsAsync(userId, ids, cancellationToken);
        return ids.Count;
    }

    // ON CONFLICT DO NOTHING: marking read twice (two tabs) is harmless.
    private async Task InsertReadsAsync(string userId, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        foreach (var id in ids)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_reads (notification_id, user_id, read_at)
                VALUES ({id}, {userId}, {now})
                ON CONFLICT (notification_id, user_id) DO NOTHING
                """, cancellationToken);
        }
    }
}