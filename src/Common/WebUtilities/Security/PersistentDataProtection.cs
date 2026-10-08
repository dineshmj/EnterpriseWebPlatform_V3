using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EnterpriseWebPlatform.Common.WebUtilities.Security;

/// <summary>The schema that holds one application's key ring (e.g. "payments_bff" in EwpBffStateDb, "identity_server" in EwpIdentityAccessDb).</summary>
public sealed record DataProtectionKeysSchema(string Name);

/// <summary>The application's Data Protection keys, in its own schema (table data_protection_keys).</summary>
public sealed class DataProtectionKeysContext(DbContextOptions<DataProtectionKeysContext> options, DataProtectionKeysSchema schema)
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
    /// <summary>Configuration keys of the certificate that encrypts the key ring at rest.</summary>
    public const string CertificatePathKey = "DataProtection:CertificatePath";
    public const string CertificatePasswordKey = "DataProtection:CertificatePassword";

    /// <summary>
    /// Keeps the application's Data Protection keys - which encrypt its cookies, anti-forgery
    /// tokens and (in the IDP) stored grants - in PostgreSQL instead of memory or the local
    /// disk: a restart invalidates nothing, and several instances read each other's cookies.
    /// The key ring is named per application (<paramref name="applicationName"/>), so
    /// applications never share keys.
    /// <para>
    /// The keys themselves are encrypted at rest, and the application refuses to start rather
    /// than store them readable (fail closed): with the certificate in
    /// <c>DataProtection:CertificatePath</c> when configured (required outside Development, and
    /// shareable by every instance), otherwise - Development on Windows only - with DPAPI, which
    /// ties them to this Windows account on this machine.
    /// </para>
    /// </summary>
    public static WebApplicationBuilder AddEwpPersistentDataProtection(
        this WebApplicationBuilder builder, string connectionString, string schema, string applicationName)
    {
        builder.Services.AddSingleton(new DataProtectionKeysSchema(schema));
        builder.Services.AddDbContext<DataProtectionKeysContext>(o => o.UseNpgsql(connectionString));

        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToDbContext<DataProtectionKeysContext>();

        var certificatePath = builder.Configuration[CertificatePathKey];
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            if (!File.Exists(certificatePath))
                throw new InvalidOperationException($"{CertificatePathKey} points to '{certificatePath}', which does not exist.");

            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, builder.Configuration[CertificatePasswordKey]);
            // Encrypt new keys with it, and decrypt with it without needing the machine's certificate store.
            dataProtection.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
        }
        else if (builder.Environment.IsDevelopment() && OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi();
        }
        else
        {
            throw new InvalidOperationException(
                $"{applicationName}: the Data Protection key ring would be stored unencrypted. Configure " +
                $"{CertificatePathKey} (and {CertificatePasswordKey}) with the certificate that encrypts it " +
                "(DPAPI is used only in Development on Windows).");
        }

        return builder;
    }
}