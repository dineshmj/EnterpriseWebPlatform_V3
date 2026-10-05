using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Common;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Persistence;

public sealed class DocumentsManagementDbContext(DbContextOptions<DocumentsManagementDbContext> options)
    : DbContext(options), IInboxStore
{
    public DbSet<Document> Documents => Set<Document>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public Task<bool> HasProcessedAsync(Guid messageId, string consumer, CancellationToken cancellationToken) =>
        InboxMessages.AsNoTracking().AnyAsync(x => x.MessageId == messageId && x.Consumer == consumer, cancellationToken);

    /// <summary>Records the message in the same transaction as the change it caused (unique per consumer).</summary>
    public void RecordProcessed(Guid messageId, string consumer, DateTimeOffset now) =>
        InboxMessages.Add(new InboxMessage { Id = Guid.NewGuid(), MessageId = messageId, Consumer = consumer, ReceivedAt = now, ProcessedAt = now });

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var aggregates = ChangeTracker.Entries<AggregateRoot>().Select(e => e.Entity).ToList();
        var result = await base.SaveChangesAsync(cancellationToken);

        // No Outbox in Documents Management yet (no consumer): the domain events have
        // served their purpose once the state is saved. See AggregateRoot.
        aggregates.ForEach(a => a.ClearDomainEvents());
        return result;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(x => x.Id);
            entity.Ignore(x => x.DomainEvents);

            entity.Property(x => x.Id)
                .HasColumnName("id");

            entity.Property(x => x.FileName)
                .HasColumnName("file_name")
                .HasConversion(v => v.Value, v => FileName.Create(v))
                .HasMaxLength(FileName.MaxLength)
                .IsRequired();

            entity.Property(x => x.ContentType)
                .HasColumnName("content_type")
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(x => x.Size)
                .HasColumnName("size")
                .IsRequired();

            entity.Property(x => x.ContentHash)
                .HasColumnName("content_hash")
                .HasConversion(v => v.Value, v => ContentHash.Create(v))
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(x => x.StorageReference)
                .HasColumnName("storage_reference")
                .HasMaxLength(1024)
                .IsRequired();

            entity.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            entity.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .IsRequired();

            entity.Property(x => x.Version)
                .HasColumnName("version")
                .IsConcurrencyToken()
                .IsRequired();

            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasConversion(v => v.ToCode(), v => DocumentStatusCode.FromCode(v))
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(x => x.InvalidatedAt)
                .HasColumnName("invalidated_at");

            entity.Property(x => x.InvalidationReason)
                .HasColumnName("invalidation_reason")
                .HasMaxLength(Document.MaxInvalidationReasonLength);

            entity.Property(x => x.DocumentType)
                .HasColumnName("document_type")
                .HasMaxLength(100);

            entity.Property(x => x.BusinessReference)
                .HasColumnName("business_reference")
                .HasMaxLength(255);

            entity.Property(x => x.ResourceBranch)
                .HasColumnName("resource_branch")
                .HasConversion(v => v!.Value, v => BranchCode.Create(v))
                .HasMaxLength(20);

            entity.HasIndex(x => new { x.ResourceBranch, x.BusinessReference, x.DocumentType });

            entity.HasIndex(x => x.ContentHash);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.MessageId).HasColumnName("message_id").IsRequired();
            entity.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(200).IsRequired();
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at").IsRequired();
            entity.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            entity.HasIndex(x => new { x.MessageId, x.Consumer }).IsUnique();
        });
    }
}

/// <summary>Idempotent consumer record: one row per (message, consumer) processed.</summary>
public sealed class InboxMessage
{
    public Guid Id { get; init; }

    public Guid MessageId { get; init; }

    public string Consumer { get; init; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }
}