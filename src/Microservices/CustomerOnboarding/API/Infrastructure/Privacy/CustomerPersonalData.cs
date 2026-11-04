using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Privacy;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Entities;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

// ┌─ :: SECURITY :: Personal fields: encrypted on save, decrypted on load ─────────────────────────
// │ Why : Crypto-shredding works only if NO copy of a personal field is ever written in clear -
// │       not the row, not a later update, not a query's projection.
// │ How : Saving (CustomerDbContext) encrypts the name, e-mail, phone and address lines of every
// │       added or changed customer under that customer's key (made with the customer) and
// │       refreshes the blind indexes. Loading an aggregate (the repository) decrypts them back;
// │       the read side decrypts only the customers on its page. A customer whose key is gone
// │       cannot be written again, and reads as "erased" - never as ciphertext.
// │ Ref : PersonalDataCipher | GDPR Art. 17, Art. 32 | OWASP A02 Cryptographic Failures
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class CustomerPersonalData(CustomerDbContext db, PersonalDataCipher cipher) : ICustomerPersonalData
{
    /// <summary>Encrypts the personal fields of every customer this unit of work adds or changes.</summary>
    public static async Task ProtectAsync(CustomerDbContext db, PersonalDataCipher cipher, CancellationToken cancellationToken)
    {
        var changed = db.ChangeTracker.Entries<Customer>()
            .Where(entry => IsWritten(entry) || entry.Entity.Addresses.Any(address => IsWritten(db.Entry(address))))
            .ToList();

        foreach (var entry in changed)
        {
            var customer = entry.Entity;
            var customerNumber = customer.CustomerNumber.Value;
            var wrappedKey = await KeyOfAsync(db, customerNumber, cancellationToken);

            if (wrappedKey is null && entry.State == EntityState.Added)
            {
                var key = new PersonalDataKey(customerNumber, cipher.NewWrappedDataKey(), DateTimeOffset.UtcNow);
                db.Set<PersonalDataKey>().Add(key);
                wrappedKey = key.WrappedKey;
            }

            if (wrappedKey is null)
                throw new DomainRuleViolationException("The customer's personal data was erased: it cannot be written again.");

            if (IsWritten(entry))
            {
                entry.Property(PersonalDataColumns.FirstName).CurrentValue = cipher.Encrypt(wrappedKey, customerNumber, customer.Name.FirstName);
                entry.Property(PersonalDataColumns.LastName).CurrentValue = cipher.Encrypt(wrappedKey, customerNumber, customer.Name.LastName);
                entry.Property(PersonalDataColumns.Email).CurrentValue = cipher.Encrypt(wrappedKey, customerNumber, customer.Email.Value);
                entry.Property(PersonalDataColumns.PhoneNumber).CurrentValue = cipher.Encrypt(wrappedKey, customerNumber, customer.PhoneNumber.Value);
                entry.Property(PersonalDataColumns.EmailIndex).CurrentValue = cipher.BlindIndex(NormaliseEmail(customer.Email.Value));
                entry.Property(PersonalDataColumns.LastNameIndex).CurrentValue = cipher.BlindIndex(NormaliseSurname(customer.Name.LastName));
            }

            foreach (var address in customer.Addresses)
            {
                var addressEntry = db.Entry(address);
                if (!IsWritten(addressEntry) && entry.State != EntityState.Added)
                    continue;

                var postal = address.Address;
                addressEntry.Property(PersonalDataColumns.AddressLine1).CurrentValue = cipher.Encrypt(wrappedKey, customerNumber, postal.AddressLine1);
                addressEntry.Property(PersonalDataColumns.AddressLine2).CurrentValue =
                    postal.AddressLine2 is null ? null : cipher.Encrypt(wrappedKey, customerNumber, postal.AddressLine2);
                addressEntry.Property(PersonalDataColumns.City).CurrentValue = postal.City;
                addressEntry.Property(PersonalDataColumns.State).CurrentValue = postal.State;
                addressEntry.Property(PersonalDataColumns.PostalCode).CurrentValue = postal.PostalCode;
                addressEntry.Property(PersonalDataColumns.CountryCode).CurrentValue = postal.CountryCode;
            }
        }
    }

    /// <summary>Decrypts the personal fields of a customer aggregate just loaded (and of its loaded addresses).</summary>
    public async Task RevealAsync(Customer customer, CancellationToken cancellationToken)
    {
        var customerNumber = customer.CustomerNumber.Value;
        var wrappedKey = await KeyOfAsync(db, customerNumber, cancellationToken)
            ?? throw new DomainRuleViolationException("The customer's personal data was erased.");

        var entry = db.Entry(customer);
        string Open(EntityEntry source, string column) =>
            cipher.Decrypt(wrappedKey, customerNumber, (string)source.Property(column).CurrentValue!);

        customer.RestorePersonalData(
            PersonName.Create(Open(entry, PersonalDataColumns.FirstName), Open(entry, PersonalDataColumns.LastName)),
            EmailAddress.Create(Open(entry, PersonalDataColumns.Email)),
            PhoneNumber.Create(Open(entry, PersonalDataColumns.PhoneNumber)));

        foreach (var address in customer.Addresses)
        {
            var addressEntry = db.Entry(address);
            var line2 = (string?)addressEntry.Property(PersonalDataColumns.AddressLine2).CurrentValue;
            address.RestoreAddress(PostalAddress.Create(
                Open(addressEntry, PersonalDataColumns.AddressLine1),
                line2 is null ? null : cipher.Decrypt(wrappedKey, customerNumber, line2),
                (string)addressEntry.Property(PersonalDataColumns.City).CurrentValue!,
                (string)addressEntry.Property(PersonalDataColumns.State).CurrentValue!,
                (string)addressEntry.Property(PersonalDataColumns.PostalCode).CurrentValue!,
                (string)addressEntry.Property(PersonalDataColumns.CountryCode).CurrentValue!));
        }
    }

    public async Task<IReadOnlyDictionary<long, CustomerPersonalDetails>> ReadAsync(
        IReadOnlyCollection<long> customerIds,
        CancellationToken cancellationToken)
    {
        if (customerIds.Count == 0)
            return new Dictionary<long, CustomerPersonalDetails>();

        var rows = await db.Customers
            .AsNoTracking()
            .Where(x => customerIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                CustomerNumber = (string)(object)x.CustomerNumber,
                FirstName = EF.Property<string>(x, PersonalDataColumns.FirstName),
                LastName = EF.Property<string>(x, PersonalDataColumns.LastName),
                Email = EF.Property<string>(x, PersonalDataColumns.Email),
                PhoneNumber = EF.Property<string>(x, PersonalDataColumns.PhoneNumber),
                Residential = x.Addresses
                    .Where(a => a.AddressType == AddressType.Residential)
                    .OrderByDescending(a => a.IsPrimary)
                    .Select(a => new
                    {
                        Line1 = EF.Property<string>(a, PersonalDataColumns.AddressLine1),
                        Line2 = EF.Property<string?>(a, PersonalDataColumns.AddressLine2),
                        City = EF.Property<string>(a, PersonalDataColumns.City),
                        State = EF.Property<string>(a, PersonalDataColumns.State),
                        PostalCode = EF.Property<string>(a, PersonalDataColumns.PostalCode),
                        CountryCode = EF.Property<string>(a, PersonalDataColumns.CountryCode)
                    })
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var customerNumbers = rows.Select(x => x.CustomerNumber).ToList();
        var keys = await db.Set<PersonalDataKey>()
            .AsNoTracking()
            .Where(x => customerNumbers.Contains(x.CustomerNumber))
            .ToDictionaryAsync(x => x.CustomerNumber, x => x.WrappedKey, cancellationToken);

        var details = new Dictionary<long, CustomerPersonalDetails>(rows.Count);
        foreach (var row in rows)
        {
            if (!keys.TryGetValue(row.CustomerNumber, out var wrappedKey))
            {
                details[row.Id] = CustomerPersonalDetails.ErasedDetails;
                continue;
            }

            string Open(string value) => cipher.Decrypt(wrappedKey, row.CustomerNumber, value);

            details[row.Id] = new CustomerPersonalDetails(
                Erased: false,
                Open(row.FirstName),
                Open(row.LastName),
                Open(row.Email),
                Open(row.PhoneNumber),
                row.Residential is null
                    ? null
                    : new ResidentialAddressDetails(
                        Open(row.Residential.Line1),
                        row.Residential.Line2 is null ? null : Open(row.Residential.Line2),
                        row.Residential.City,
                        row.Residential.State,
                        row.Residential.PostalCode,
                        row.Residential.CountryCode));
        }

        return details;
    }

    public IQueryable<Customer> WhereEmailOrSurnameIs(IQueryable<Customer> customers, string value)
    {
        var emailIndex = cipher.BlindIndex(NormaliseEmail(value));
        var surnameIndex = cipher.BlindIndex(NormaliseSurname(value));

        return customers.Where(x =>
            EF.Property<string>(x, PersonalDataColumns.EmailIndex) == emailIndex ||
            EF.Property<string>(x, PersonalDataColumns.LastNameIndex) == surnameIndex);
    }

    // One spelling per value, so "Ava.Nguyen@Example.com " and "ava.nguyen@example.com" index alike.
    internal static string NormaliseEmail(string email) => email.Trim().ToUpperInvariant();

    internal static string NormaliseSurname(string surname) =>
        string.Join(' ', surname.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToUpperInvariant();

    private static bool IsWritten(EntityEntry entry) => entry.State is EntityState.Added or EntityState.Modified;

    private static async Task<byte[]?> KeyOfAsync(CustomerDbContext db, string customerNumber, CancellationToken cancellationToken) =>
        (await db.Set<PersonalDataKey>().FindAsync([customerNumber], cancellationToken))?.WrappedKey;
}