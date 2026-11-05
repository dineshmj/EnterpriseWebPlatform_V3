using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Compliance.Api.Application.Queries;
using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Privacy;

// ┌─ :: SECURITY :: The applicant snapshot: encrypted on save, decrypted on load ──────────────────
// │ Why : Crypto-shredding works only if the applicant is never written in clear - not the row,
// │       not a query's projection.
// │ How : Saving (ComplianceDbContext) encrypts the applicant of every NEW case, as one value, under the
// │       customer's key (made with the customer's first case); the snapshot never changes after.
// │       Loading a case (the repository) decrypts it; the case lists decrypt only their page; screening decrypts the case it screens. When a
// │       case is marked erased, the customer's key is deleted in the same transaction: every case
// │       of that customer, and every backup taken from then on, reads "Personal data erased".
// │ Ref : PersonalDataCipher | doc/Crypto-Shredding.md | GDPR Art. 17, Art. 32 | OWASP A02
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ComplianceCasePersonalData(ComplianceDbContext db, PersonalDataCipher cipher)
{
    /// <summary>The shadow property (column "applicant") holding the encrypted applicant.</summary>
    public const string ApplicantColumn = "ApplicantProtected";

    /// <summary>What the screens show in place of the applicant's name once it is erased.</summary>
    public const string ErasedName = "Personal data erased";

    private static readonly ApplicantAddress NoAddress = new(null, null, null, null, null, null);

    /// <summary>Encrypts the applicant of every new case, and destroys the key of every case just erased.</summary>
    public static async Task ProtectAsync(ComplianceDbContext db, PersonalDataCipher cipher, CancellationToken cancellationToken)
    {
        var keys = db.Set<PersonalDataKey>();

        foreach (var entry in db.ChangeTracker.Entries<ComplianceCase>().ToList())
        {
            var complianceCase = entry.Entity;
            var customerNumber = complianceCase.CustomerNumber;

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ The erasure itself: the key goes in the same transaction that marks the case erased.
            // │ The ciphertext stays where it is - without the key it is unreadable.
            // └──────────────────────────────────────────────────────────────────────────────────────
            if (entry.State == EntityState.Modified &&
                complianceCase.IsPersonalDataErased &&
                entry.Property(nameof(ComplianceCase.PersonalDataErasedAt)).IsModified)
            {
                if (await keys.FindAsync([customerNumber], cancellationToken) is { } erasedKey)
                    keys.Remove(erasedKey);
                continue;
            }

            // The snapshot is written once, with the case.
            if (entry.State != EntityState.Added)
                continue;

            var applicant = complianceCase.Applicant
                ?? throw new InvalidOperationException("A new compliance case must name its applicant.");

            var wrappedKey = (await keys.FindAsync([customerNumber], cancellationToken))?.WrappedKey;
            if (wrappedKey is null)
            {
                var key = new PersonalDataKey(customerNumber, cipher.NewWrappedDataKey(), DateTimeOffset.UtcNow);
                keys.Add(key);
                wrappedKey = key.WrappedKey;
            }

            entry.Property(ApplicantColumn).CurrentValue =
                cipher.Encrypt(wrappedKey, customerNumber, JsonSerializer.Serialize(Snapshot.Of(applicant)));
        }
    }

    /// <summary>Decrypts the applicant of a case just loaded. An erased case stays without one.</summary>
    public async Task RevealAsync(ComplianceCase complianceCase, CancellationToken cancellationToken)
    {
        if (complianceCase.IsPersonalDataErased)
            return;

        var wrappedKey = (await db.Set<PersonalDataKey>().FindAsync([complianceCase.CustomerNumber], cancellationToken))?.WrappedKey
            ?? throw new InvalidOperationException(
                $"Compliance case {complianceCase.Id} is not marked erased, but its customer's key is missing.");

        var value = (string)db.Entry(complianceCase).Property(ApplicantColumn).CurrentValue!;
        complianceCase.RestoreApplicant(Open(wrappedKey, complianceCase.CustomerNumber, value));
    }

    /// <summary>
    /// The name and address to show for each row of a page, in the same order. A row whose data
    /// was erased (or whose key is gone) reads <see cref="ErasedName"/> with no address.
    /// </summary>
    public async Task<IReadOnlyList<(string Name, ApplicantAddress Address)>> ShowAsync(
        IReadOnlyList<(string CustomerNumber, string Protected, bool Erased)> rows,
        CancellationToken cancellationToken)
    {
        var customerNumbers = rows.Where(x => !x.Erased).Select(x => x.CustomerNumber).Distinct().ToList();
        var keys = customerNumbers.Count == 0
            ? new Dictionary<string, byte[]>()
            : await db.Set<PersonalDataKey>()
                .AsNoTracking()
                .Where(x => customerNumbers.Contains(x.CustomerNumber))
                .ToDictionaryAsync(x => x.CustomerNumber, x => x.WrappedKey, cancellationToken);

        return rows
            .Select(row =>
            {
                if (row.Erased || !keys.TryGetValue(row.CustomerNumber, out var wrappedKey))
                    return (ErasedName, NoAddress);

                var applicant = Open(wrappedKey, row.CustomerNumber, row.Protected);
                return (applicant.FullName, new ApplicantAddress(
                    applicant.AddressLine1,
                    applicant.AddressLine2,
                    applicant.City,
                    applicant.State,
                    applicant.PostalCode,
                    applicant.CountryCode));
            })
            .ToList();
    }

    private Applicant Open(byte[] wrappedKey, string customerNumber, string protectedValue) =>
        JsonSerializer.Deserialize<Snapshot>(cipher.Decrypt(wrappedKey, customerNumber, protectedValue))!.ToApplicant();

    // The applicant as one encrypted value: a case never needs a field of it on its own in SQL.
    private sealed record Snapshot(
        string FirstName,
        string LastName,
        string? AddressLine1,
        string? AddressLine2,
        string? City,
        string? State,
        string? PostalCode,
        string? CountryCode)
    {
        public static Snapshot Of(Applicant a) =>
            new(a.FirstName, a.LastName, a.AddressLine1, a.AddressLine2, a.City, a.State, a.PostalCode, a.CountryCode);

        public Applicant ToApplicant() =>
            Applicant.Create(FirstName, LastName, AddressLine1, AddressLine2, City, State, PostalCode, CountryCode);
    }
}