using Microsoft.EntityFrameworkCore;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Persistence;

public sealed class CustomerOutboxDbContext(
    DbContextOptions<CustomerOutboxDbContext> options)
    : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.AggregateType).HasColumnName("aggregate_type");
            entity.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            entity.Property(x => x.EventType).HasColumnName("event_type");
            entity.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            entity.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by").HasColumnType("uuid");
            entity.Property(x => x.PublishedAt).HasColumnName("published_at");
            entity.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            entity.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at");
            entity.Property(x => x.LastError).HasColumnName("last_error");
        });
    }
}