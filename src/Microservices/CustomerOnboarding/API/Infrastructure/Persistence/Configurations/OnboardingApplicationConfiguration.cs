using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Configurations;

public sealed class OnboardingApplicationConfiguration
    : IEntityTypeConfiguration<OnboardingApplication>
{
    public void Configure(EntityTypeBuilder<OnboardingApplication> builder)
    {
        builder.ToTable("onboarding_applications");

        builder.HasKey(x => x.Id)
            .HasName("pk_onboarding_applications");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.ApplicationNumber)
            .HasColumnName("application_number")
            .HasConversion(
                value => value.Value,
                value => ApplicationNumber.Create(value))
            .HasMaxLength(30)
            .IsRequired();

        builder.HasIndex(x => x.ApplicationNumber)
            .IsUnique()
            .HasDatabaseName("uq_onboarding_applications_application_number");

        builder.Property(x => x.CustomerId)
            .HasColumnName("customer_id")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.SubmittedAt)
            .HasColumnName("submitted_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.CompletedAt)
            .HasColumnName("completed_at")
            .HasColumnType("timestamp with time zone");

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

        builder.HasOne(x => x.Customer)
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .HasConstraintName("fk_onboarding_applications_customer")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.CustomerId)
            .HasDatabaseName("ix_onboarding_applications_customer_id");

        builder.HasIndex(x => x.Status)
            .HasDatabaseName("ix_onboarding_applications_status");

        builder.HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName("ix_onboarding_applications_status_created_at");
    }
}