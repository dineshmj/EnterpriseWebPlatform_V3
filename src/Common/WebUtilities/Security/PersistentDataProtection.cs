using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

/// <summary>The schema of one BFF in EwpBffStateDb (e.g. "payments_bff"): its keys and sessions live there, and only its user may use it.</summary>
public sealed record BffStateSchema(string Name);

/// <summary>The BFF's Data Protection keys, in its own schema of EwpBffStateDb (table data_protection_keys).</summary>
public sealed class DataProtectionKeysContext(DbContextOptions<DataProtectionKeysContext> options, BffStateSchema schema)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(schema.Name);
        modelBuilder.Entity<DataProtectionKey>(e =>
        {
            e.ToTable("data_protection_keys");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.FriendlyName).HasColumnName("friendly_name");
            e.Property(x => x.Xml).HasColumnName("xml");
        });
    }
}

public static class PersistentDataProtectionExtensions
{
    /// <summary>
    /// Keeps the BFF's Data Protection keys - which encrypt its session cookie and anti-forgery
    /// tokens - in PostgreSQL instead of memory: a restart no longer invalidates every cookie,
    /// and several instances of the BFF can read each other's cookies. The key ring is named
    /// per BFF (<paramref name="applicationName"/>), so BFFs never share keys. On Windows the
    /// keys are additionally encrypted at rest with DPAPI (production: a certificate or a KMS).
    /// </summary>
    public static IServiceCollection AddEwpPersistentDataProtection(
        this IServiceCollection services, string connectionString, string schema, string applicationName)
    {
        services.AddSingleton(new BffStateSchema(schema));
        services.AddDbContext<DataProtectionKeysContext>(o => o.UseNpgsql(connectionString));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToDbContext<DataProtectionKeysContext>();
        if (OperatingSystem.IsWindows())
            dataProtection.ProtectKeysWithDpapi();

        return services;
    }
}