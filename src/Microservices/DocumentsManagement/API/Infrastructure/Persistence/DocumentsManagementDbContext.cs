using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseWebPlatform.DocumentsManagement.Infrastructure.Persistence;

public sealed class DocumentsManagementDbContext(DbContextOptions<DocumentsManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(entity =>
        {
            entity.ToTable("documents");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id");

            entity.Property(x => x.FileName)
                .HasColumnName("file_name")
                .HasMaxLength(255)
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

            entity.HasIndex(x => x.ContentHash);
        });
    }
}
