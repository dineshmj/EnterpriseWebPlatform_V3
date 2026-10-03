using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Common;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Persistence;

public sealed class DocumentsManagementDbContext(DbContextOptions<DocumentsManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

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
                .IsRequired();

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
    }
}