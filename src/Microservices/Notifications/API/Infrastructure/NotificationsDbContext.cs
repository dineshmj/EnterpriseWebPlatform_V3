using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Notifications.Api.Domain;

namespace EnterpriseWebPlatform.Notifications.Api.Infrastructure;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationRead> NotificationReads => Set<NotificationRead>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.Audience).HasColumnName("audience").HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(20).IsRequired();
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
            e.Property(x => x.Body).HasColumnName("body").HasMaxLength(1000).IsRequired();
            e.Property(x => x.Target).HasColumnName("target").HasColumnType("jsonb");
            e.Property(x => x.SourceMessageId).HasColumnName("source_message_id").IsRequired();
            e.Property(x => x.SourceEventType).HasColumnName("source_event_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        });

        modelBuilder.Entity<NotificationRead>(e =>
        {
            e.ToTable("notification_reads");
            e.HasKey(x => new { x.NotificationId, x.UserId });
            e.Property(x => x.NotificationId).HasColumnName("notification_id");
            e.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(200);
            e.Property(x => x.ReadAt).HasColumnName("read_at").IsRequired();
        });

        modelBuilder.Entity<InboxMessage>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
            e.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(200).IsRequired();
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at").IsRequired();
        });
    }
}

/// <summary>Inbox (idempotent consumer): a workflow event becomes notifications exactly once.</summary>
public sealed class InboxMessage
{
    private InboxMessage() { }

    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string Consumer { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }

    public static InboxMessage Processed(Guid messageId, string consumer, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), MessageId = messageId, Consumer = consumer, ProcessedAt = now };
}