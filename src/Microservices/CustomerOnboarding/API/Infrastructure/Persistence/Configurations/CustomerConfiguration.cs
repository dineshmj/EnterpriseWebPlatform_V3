using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");

        builder.HasKey(x => x.Id)
            .HasName("pk_customers");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.CustomerNumber)
            .HasColumnName("customer_number")
            .HasConversion(
                value => value.Value,
                value => CustomerNumber.Create(value))
            .HasMaxLength(30)
            .IsRequired();

        builder.HasIndex(x => x.CustomerNumber)
            .IsUnique()
            .HasDatabaseName("uq_customers_customer_number");

        builder.Property(x => x.SubjectId)
            .HasColumnName("subject_id")
            .HasColumnType("uuid");

        builder.HasIndex(x => x.SubjectId)
            .IsUnique()
            .HasDatabaseName("uq_customers_subject_id");

        builder.Property(x => x.FirstName)
            .HasColumnName("first_name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.LastName)
            .HasColumnName("last_name")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.Email)
            .HasColumnName("email")
            .HasConversion(
                value => value.Value,
                value => EmailAddress.Create(value))
            .HasMaxLength(254)
            .IsRequired();

        builder.Property(x => x.PhoneNumber)
            .HasColumnName("phone_number")
            .HasConversion(
                value => value.Value,
                value => PhoneNumber.Create(value))
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.CustomerType)
            .HasColumnName("customer_type")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.BranchId)
            .HasColumnName("branch_id");

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .IsRequired();

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("ix_customers_status");

        builder.HasIndex(x => x.BranchId)
            .HasDatabaseName("ix_customers_branch_id");

        builder.HasIndex(x => x.Email)
            .HasDatabaseName("ix_customers_email");
    }
}