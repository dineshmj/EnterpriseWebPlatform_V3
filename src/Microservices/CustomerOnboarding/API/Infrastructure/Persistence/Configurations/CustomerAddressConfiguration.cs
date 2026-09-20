using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;

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
            .HasConversion<string>()
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

        builder.ComplexProperty(
            x => x.Address,
            address =>
            {
                address.Property(x => x.AddressLine1)
                    .HasColumnName("address_line1")
                    .HasMaxLength(200)
                    .IsRequired();

                address.Property(x => x.AddressLine2)
                    .HasColumnName("address_line2")
                    .HasMaxLength(200);

                address.Property(x => x.City)
                    .HasColumnName("city")
                    .HasMaxLength(100)
                    .IsRequired();

                address.Property(x => x.State)
                    .HasColumnName("state")
                    .HasMaxLength(100)
                    .IsRequired();

                address.Property(x => x.PostalCode)
                    .HasColumnName("postal_code")
                    .HasMaxLength(20)
                    .IsRequired();

                address.Property(x => x.CountryCode)
                    .HasColumnName("country_code")
                    .HasColumnType("character(2)")
                    .IsRequired();
            });

        builder.HasOne(x => x.Customer)
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