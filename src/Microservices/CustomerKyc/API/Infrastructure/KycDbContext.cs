using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerKyc.Api.Domain;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

public sealed class KycDbContext(DbContextOptions<KycDbContext> options) : DbContext(options)
{
    public DbSet<KycCase> KycCases => Set<KycCase>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KycCase>(e =>
        {
            e.ToTable("kyc_cases");
            e.HasKey(x => x.Id).HasName("pk_kyc_cases");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(50).IsRequired();
            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired();
            e.HasIndex(x => x.CustomerNumber).IsUnique().HasDatabaseName("uq_kyc_cases_customer_number");
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id).HasName("pk_kyc_outbox_messages");
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.AggregateType).HasColumnName("aggregate_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id").HasMaxLength(100).IsRequired();
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(200).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
            e.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0).IsRequired();
            e.Property(x => x.LastAttemptAt).HasColumnName("last_attempt_at").HasColumnType("timestamp with time zone");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.HasIndex(x => x.OccurredAt).HasDatabaseName("ix_kyc_outbox_unpublished").HasFilter("published_at IS NULL");
        });
    }
}