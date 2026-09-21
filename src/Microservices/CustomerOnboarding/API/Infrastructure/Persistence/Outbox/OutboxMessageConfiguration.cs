using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;

public sealed class OutboxMessageConfiguration
    : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(x => x.Id)
            .HasName("pk_outbox_messages");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(x => x.AggregateType)
            .HasColumnName("aggregate_type")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.AggregateId)
            .HasColumnName("aggregate_id")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(x => x.OccurredAt)
            .HasColumnName("occurred_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.PublishedAt)
            .HasColumnName("published_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.AttemptCount)
            .HasColumnName("attempt_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(x => x.LastAttemptAt)
            .HasColumnName("last_attempt_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.LastError)
            .HasColumnName("last_error");

        builder.HasIndex(x => x.OccurredAt)
            .HasDatabaseName("ix_outbox_messages_unpublished")
            .HasFilter("published_at IS NULL");

        builder.HasIndex(x => new
        {
            x.AggregateType,
            x.AggregateId
        })
        .HasDatabaseName("ix_outbox_messages_aggregate");
    }
}
