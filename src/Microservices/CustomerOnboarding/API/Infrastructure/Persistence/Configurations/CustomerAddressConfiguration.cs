using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Configurations;

public sealed class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> builder)
    {
        builder.ToTable("customer_addresses");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(x => x.CustomerId)
            .HasColumnName("customer_id")
            .IsRequired();

        builder.Property(x => x.AddressType)
            .HasColumnName("address_type")
            // Upper case, matching ck_customer_addresses_address_type and the
            // convention used for the other customer enums.
            .HasConversion(
                value => value.ToString().ToUpperInvariant(),
                value => Enum.Parse<AddressType>(value, ignoreCase: true))
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.IsPrimary)
            .HasColumnName("is_primary")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ The address lines are stored ENCRYPTED under the customer's own key; city, state,
        // │ postcode and country stay readable - on their own they identify nobody, and the
        // │ branch scope (CustomerAccessScope) filters on the city in SQL. The domain's
        // │ PostalAddress is therefore not mapped: shadow columns are, and the repository
        // │ rebuilds the PostalAddress on load.
        // └──────────────────────────────────────────────────────────────────────────────────────
        builder.Ignore(x => x.Address);

        builder.Property<string>(PersonalDataColumns.AddressLine1)
            .HasColumnName("address_line1")
            .IsRequired();

        builder.Property<string?>(PersonalDataColumns.AddressLine2)
            .HasColumnName("address_line2");

        builder.Property<string>(PersonalDataColumns.City)
            .HasColumnName("city")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property<string>(PersonalDataColumns.State)
            .HasColumnName("state")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property<string>(PersonalDataColumns.PostalCode)
            .HasColumnName("postal_code")
            .HasMaxLength(20)
            .IsRequired();

        builder.Property<string>(PersonalDataColumns.CountryCode)
            .HasColumnName("country_code")
            .HasColumnType("character(2)")
            .IsRequired();

        builder.HasOne<Domain.Aggregates.Customer>()
            .WithMany(x => x.Addresses)
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.CustomerId)
            .HasDatabaseName("ix_customer_addresses_customer_id");

        builder.HasIndex(x => new
        {
            x.CustomerId,
            x.AddressType
        })
        .HasDatabaseName("ix_customer_addresses_customer_address_type");
    }
}