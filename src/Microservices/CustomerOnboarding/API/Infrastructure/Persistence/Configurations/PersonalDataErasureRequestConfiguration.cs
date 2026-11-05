using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Configurations;

public sealed class PersonalDataErasureRequestConfiguration : IEntityTypeConfiguration<PersonalDataErasureRequest>
{
    public void Configure(EntityTypeBuilder<PersonalDataErasureRequest> builder)
    {
        builder.ToTable("personal_data_erasure_requests");

        builder.HasKey(x => x.Id)
            .HasName("pk_personal_data_erasure_requests");

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.CustomerId)
            .HasColumnName("customer_id")
            .IsRequired();

        builder.Property(x => x.CustomerNumber)
            .HasColumnName("customer_number")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.Reason)
            .HasColumnName("reason_code")
            .HasConversion(
                value => value.ToCode(),
                value => FromCode(value))
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion(
                value => value.ToString().ToUpperInvariant(),
                value => Enum.Parse<PersonalDataErasureStatus>(value, ignoreCase: true))
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(x => x.RequestedByUserId)
            .HasColumnName("requested_by_user_id")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(x => x.RequestedAt)
            .HasColumnName("requested_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(x => x.DecidedByUserId)
            .HasColumnName("decided_by_user_id")
            .HasMaxLength(200);

        builder.Property(x => x.DecidedAt)
            .HasColumnName("decided_at")
            .HasColumnType("timestamp with time zone");

        builder.Property(x => x.DecisionRemarks)
            .HasColumnName("decision_remarks")
            .HasMaxLength(PersonalDataErasureRequest.MaxRemarksLength);

        builder.Property(x => x.Version)
            .HasColumnName("version")
            .IsConcurrencyToken()
            .IsRequired();

        builder.HasIndex(x => x.CustomerId)
            .HasDatabaseName("ix_personal_data_erasure_requests_customer_id");
    }

    private static PersonalDataErasureReason FromCode(string code) =>
        PersonalDataErasureReasonCode.TryFromCode(code, out var reason)
            ? reason
            : throw new InvalidOperationException($"Unknown erasure reason code '{code}' in the database.");
}