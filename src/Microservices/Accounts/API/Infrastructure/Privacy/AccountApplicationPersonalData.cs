using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Privacy;

// ┌─ :: SECURITY :: The holder's name on an application: encrypted on save, decrypted on load ─────
// │ Why : Crypto-shredding works only if the name is never written in clear - not the row, not
// │       a query's projection.
// │ How : Saving (AccountsDbContext) encrypts the holder of every NEW application, as one value,
// │       under the customer's key (made with the customer's first application); it never changes
// │       after. Loading an application (the repository) decrypts it; the queue decrypts only its
// │       page. When an application is marked erased, the customer's key is deleted in the same
// │       transaction: every application of that customer reads "Personal data erased".
// │       Opened accounts keep their holder in clear: only an ACTIVE customer has one, and an
// │       active customer's data is never erased (doc/Crypto-Shredding.md).
// │ Ref : PersonalDataCipher | GDPR Art. 17, Art. 32 | OWASP A02
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class AccountApplicationPersonalData(AccountsDbContext db, PersonalDataCipher cipher)
{
    /// <summary>The shadow property (column "holder_name") holding the encrypted holder's name.</summary>
    public const string HolderNameColumn = "HolderNameProtected";

    /// <summary>What the screens show in place of the holder's name once it is erased.</summary>
    public const string ErasedName = "Personal data erased";

    /// <summary>Encrypts the holder of every new application, and destroys the key of every application just erased.</summary>
    public static async Task ProtectAsync(AccountsDbContext db, PersonalDataCipher cipher, CancellationToken cancellationToken)
    {
        var keys = db.Set<PersonalDataKey>();

        foreach (var entry in db.ChangeTracker.Entries<AccountApplication>().ToList())
        {
            var application = entry.Entity;
            var customerNumber = application.CustomerNumber;

            // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
            // │ The erasure itself: the key goes in the same transaction that marks the application
            // │ erased. The ciphertext stays where it is - without the key it is unreadable.
            // └──────────────────────────────────────────────────────────────────────────────────────
            if (entry.State == EntityState.Modified &&
                application.IsPersonalDataErased &&
                entry.Property(nameof(AccountApplication.PersonalDataErasedAt)).IsModified)
            {
                if (await keys.FindAsync([customerNumber], cancellationToken) is { } erasedKey)
                    keys.Remove(erasedKey);
                continue;
            }

            // The name is written once, with the application.
            if (entry.State != EntityState.Added)
                continue;

            var holder = application.HolderName
                ?? throw new InvalidOperationException("A new account application must name its holder.");

            var wrappedKey = (await keys.FindAsync([customerNumber], cancellationToken))?.WrappedKey;
            if (wrappedKey is null)
            {
                var key = new PersonalDataKey(customerNumber, cipher.NewWrappedDataKey(), DateTimeOffset.UtcNow);
                keys.Add(key);
                wrappedKey = key.WrappedKey;
            }

            entry.Property(HolderNameColumn).CurrentValue =
                cipher.Encrypt(wrappedKey, customerNumber, JsonSerializer.Serialize(new Snapshot(holder.FirstName, holder.LastName)));
        }
    }

    /// <summary>Decrypts the holder of an application just loaded. An erased application stays without one.</summary>
    public async Task RevealAsync(AccountApplication application, CancellationToken cancellationToken)
    {
        if (application.IsPersonalDataErased)
            return;

        var wrappedKey = (await db.Set<PersonalDataKey>().FindAsync([application.CustomerNumber], cancellationToken))?.WrappedKey
            ?? throw new InvalidOperationException(
                $"Account application {application.Id} is not marked erased, but its customer's key is missing.");

        var value = (string)db.Entry(application).Property(HolderNameColumn).CurrentValue!;
        application.RestoreHolderName(Open(wrappedKey, application.CustomerNumber, value));
    }

    /// <summary>
    /// The name to show for each row of a page, in the same order. A row whose data was erased
    /// (or whose key is gone) reads <see cref="ErasedName"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> ShowAsync(
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
            .Select(row => row.Erased || !keys.TryGetValue(row.CustomerNumber, out var wrappedKey)
                ? ErasedName
                : Open(wrappedKey, row.CustomerNumber, row.Protected).FullName)
            .ToList();
    }

    private HolderName Open(byte[] wrappedKey, string customerNumber, string protectedValue)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(cipher.Decrypt(wrappedKey, customerNumber, protectedValue))!;
        return HolderName.Create(snapshot.FirstName, snapshot.LastName);
    }

    private sealed record Snapshot(string FirstName, string LastName);
}