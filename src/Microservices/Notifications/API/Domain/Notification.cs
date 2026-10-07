using System.Security.Claims;

namespace EnterpriseWebPlatform.Notifications.Api.Domain;

/// <summary>
/// One notification for one audience: a single person ("user:{sub}") or the staff of one
/// role in one branch ("staff:{role}:{branch}", e.g. new work for the KYC officers of
/// SYD001). Immutable once created; read state is kept per person (<see cref="NotificationRead"/>),
/// so a branch-wide notification can be read by each officer independently.
/// </summary>
public sealed class Notification
{
    private Notification() { }

    public long Id { get; private set; }

    public string Audience { get; private set; } = string.Empty;

    /// <summary><c>PROGRESS</c> (an application of yours moved) or <c>NEW_WORK</c> (work arrived for your team).</summary>
    public string Category { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    /// <summary>
    /// The record the notification is about, as JSON - e.g. {"mfe":"kyc","page":"cases/view-details","recordId":3}.
    /// Stored now; the Shell will open it in a later increment (deep links).
    /// </summary>
    public string? Target { get; private set; }

    /// <summary>The workflow event this notification came from (idempotency together with the audience).</summary>
    public Guid SourceMessageId { get; private set; }

    public string SourceEventType { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Notification Create(NotificationDraft draft, Guid sourceMessageId, string sourceEventType, DateTimeOffset now) => new()
    {
        Audience = draft.Audience,
        Category = draft.Category,
        Title = Limit(draft.Title, 200),
        Body = Limit(draft.Body, 1000),
        Target = draft.Target,
        SourceMessageId = sourceMessageId,
        SourceEventType = sourceEventType,
        CreatedAt = now
    };

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>A person has read a notification (per person, also for branch-wide ones).</summary>
public sealed class NotificationRead
{
    private NotificationRead() { }

    public long NotificationId { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    public DateTimeOffset ReadAt { get; private set; }

    public static NotificationRead Create(long notificationId, string userId, DateTimeOffset now) =>
        new() { NotificationId = notificationId, UserId = userId, ReadAt = now };
}

/// <summary>What the rules decided: who is told what. Not yet stored.</summary>
public sealed record NotificationDraft(string Audience, string Category, string Title, string Body, string? Target);

public static class NotificationCategory
{
    public const string Progress = "PROGRESS";
    public const string NewWork = "NEW_WORK";
}

/// <summary>
/// Audiences, derived on the server from the person's token - never chosen by the browser,
/// so nobody can listen to another person's or another branch's notifications.
/// </summary>
public static class NotificationAudiences
{
    /// <summary>Roles whose staff receive their branch's new-work notifications.</summary>
    public static readonly IReadOnlySet<string> WorkQueueRoles =
        new HashSet<string>(StringComparer.Ordinal) { "kyc_officer", "compliance_officer", "account_officer", "payments_officer" };

    public static string ForUser(string userId) => $"user:{userId.Trim().ToLowerInvariant()}";

    public static string ForStaff(string role, string branch) => $"staff:{role}:{branch.Trim().ToUpperInvariant()}";

    /// <summary>Every audience the signed-in person belongs to: themselves, plus each work-queue role in their branch.</summary>
    public static IReadOnlyList<string> Of(ClaimsPrincipal user)
    {
        var sub = user.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(sub))
            return [];

        var audiences = new List<string> { ForUser(sub) };
        var branch = user.FindFirst("branch")?.Value;
        if (!string.IsNullOrWhiteSpace(branch))
        {
            audiences.AddRange(user.FindAll("role")
                .Select(r => r.Value)
                .Where(WorkQueueRoles.Contains)
                .Distinct()
                .Select(role => ForStaff(role, branch)));
        }

        return audiences;
    }
}