using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

/// <summary>One customer's data key, wrapped by the master key. Deleting the row is the erasure.</summary>
public sealed class PersonalDataKey
{
    private PersonalDataKey()
    {
        CustomerNumber = null!;
        WrappedKey = null!;
    }

    public PersonalDataKey(string customerNumber, byte[] wrappedKey, DateTimeOffset createdAt)
    {
        CustomerNumber = customerNumber;
        WrappedKey = wrappedKey;
        CreatedAt = createdAt;
    }

    public string CustomerNumber { get; private set; }

    public byte[] WrappedKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class PersonalDataKeyConfiguration : IEntityTypeConfiguration<PersonalDataKey>
{
    public void Configure(EntityTypeBuilder<PersonalDataKey> builder)
    {
        builder.ToTable("personal_data_keys");

        builder.HasKey(x => x.CustomerNumber)
            .HasName("pk_personal_data_keys");

        builder.Property(x => x.CustomerNumber)
            .HasColumnName("customer_number")
            .HasMaxLength(30);

        builder.Property(x => x.WrappedKey)
            .HasColumnName("wrapped_key")
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}

/// <summary>Names of the shadow properties that hold the encrypted (and blind-indexed) personal fields.</summary>
internal static class PersonalDataColumns
{
    public const string FirstName = "FirstNameProtected";
    public const string LastName = "LastNameProtected";
    public const string Email = "EmailProtected";
    public const string PhoneNumber = "PhoneNumberProtected";
    public const string EmailIndex = "EmailIndex";
    public const string LastNameIndex = "LastNameIndex";

    public const string AddressLine1 = "AddressLine1Protected";
    public const string AddressLine2 = "AddressLine2Protected";
    public const string City = "City";
    public const string State = "State";
    public const string PostalCode = "PostalCode";
    public const string CountryCode = "CountryCode";
}