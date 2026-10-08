using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Audit.Api.Domain;

namespace EnterpriseWebPlatform.Audit.Api.Infrastructure;

/// <summary>EwpAuditDb: the audit trail (table audit_entries; see AuditDb/EwpAuditDb.sql).</summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_entries");
            e.HasKey(x => x.Sequence);
            e.Property(x => x.Sequence).HasColumnName("sequence").ValueGeneratedNever();
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.HasIndex(x => x.MessageId).IsUnique();
            e.Property(x => x.EntryKind).HasColumnName("entry_kind").HasMaxLength(10);
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100);
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(100);
            e.Property(x => x.Topic).HasColumnName("topic").HasMaxLength(200);
            e.Property(x => x.KafkaPartition).HasColumnName("kafka_partition");
            e.Property(x => x.KafkaOffset).HasColumnName("kafka_offset");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.RecordedAt).HasColumnName("recorded_at");
            e.Property(x => x.WorkflowId).HasColumnName("workflow_id");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CausationId).HasColumnName("causation_id");
            e.Property(x => x.InitiatedByUserId).HasColumnName("initiated_by_user_id").HasMaxLength(200);
            e.Property(x => x.InitiatedByLanId).HasColumnName("initiated_by_lan_id").HasMaxLength(50);
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(200);
            e.Property(x => x.ActorLanId).HasColumnName("actor_lan_id").HasMaxLength(50);
            e.Property(x => x.RecordType).HasColumnName("record_type").HasMaxLength(20);
            e.Property(x => x.RecordRef).HasColumnName("record_ref").HasMaxLength(100);
            e.Property(x => x.CustomerNumber).HasColumnName("customer_number").HasMaxLength(50);
            e.Property(x => x.BranchCode).HasColumnName("branch_code").HasMaxLength(20);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(100);
            e.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(100);
            e.Property(x => x.Amount).HasColumnName("amount").HasPrecision(18, 2);
            e.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3);
            e.Property(x => x.PayloadSha256).HasColumnName("payload_sha256").HasMaxLength(64);
            e.Property(x => x.PreviousHash).HasColumnName("previous_hash").HasMaxLength(64);
            e.Property(x => x.EntryHash).HasColumnName("entry_hash").HasMaxLength(64);
        });
    }
}