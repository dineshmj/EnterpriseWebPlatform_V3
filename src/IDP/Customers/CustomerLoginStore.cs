using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.IdentityServer.Data;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Customers;

// ┌─ :: SECURITY :: Customer passwords and lockout ────────────────────────────────────────────────
// │ Why : The customer sign-in faces the internet: it must survive password guessing and must not
// │       tell a stranger which customer numbers have online access.
// │ How : PBKDF2 hashes (ASP.NET Core Identity's hasher); an unknown customer number costs the same
// │       work as a known one (a dummy hash is checked) and gets the same answer; five wrong
// │       passwords lock the sign-in for 15 minutes, counted in ONE UPDATE (no lost count); the IDP's
// │       per-IP throttle sits in front of every page.
// │ Ref : Features 2.1.6 | OWASP A07 | ASVS V2.2.1, V2.4 | NIST SP 800-63B §5.1.1
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Customers' sign-ins: credentials, lockout and creation at the end of a registration.</summary>
public sealed class CustomerLoginStore(IdentityDbContext db, TimeProvider time)
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>The password rule: 12 to 128 characters, not containing the customer number (NIST: length, no composition rules).</summary>
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 128;

    private static readonly PasswordHasher<CustomerLogin> Hasher = new();
    private static readonly string DummyHash = Hasher.HashPassword(new CustomerLogin(), Guid.NewGuid().ToString("N"));

    public static string Normalize(string? customerNumber) => (customerNumber ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Why a chosen password is refused, or null when it is acceptable.</summary>
    public static string? PasswordProblem(string customerNumber, string? password, string? confirmation)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
            return $"Choose a password of at least {MinimumPasswordLength} characters.";
        if (password.Length > MaximumPasswordLength)
            return $"Choose a password of at most {MaximumPasswordLength} characters.";
        if (password.Contains(customerNumber, StringComparison.OrdinalIgnoreCase))
            return "The password must not contain your customer number.";
        if (password != confirmation)
            return "The two passwords are not the same.";
        return null;
    }

    public Task<CustomerLogin?> FindBySubjectIdAsync(string subjectId, CancellationToken cancellationToken) =>
        Guid.TryParse(subjectId, out var subject)
            ? db.CustomerLogins.AsNoTracking().SingleOrDefaultAsync(c => c.SubjectId == subject, cancellationToken)
            : Task.FromResult<CustomerLogin?>(null);

    public Task<bool> ExistsAsync(string customerNumber, CancellationToken cancellationToken) =>
        db.CustomerLogins.AnyAsync(c => c.CustomerNumber == customerNumber, cancellationToken);

    /// <summary>The customer whose password this is - active and not locked - or null (one answer for every failure).</summary>
    public async Task<CustomerLogin?> ValidateCredentialsAsync(string customerNumber, string password, CancellationToken cancellationToken)
    {
        var number = Normalize(customerNumber);
        var login = await db.CustomerLogins.AsNoTracking().SingleOrDefaultAsync(c => c.CustomerNumber == number, cancellationToken);
        if (login is null)
        {
            Hasher.VerifyHashedPassword(new CustomerLogin(), DummyHash, password ?? string.Empty);
            return null;
        }

        var now = time.GetUtcNow();
        if (!login.IsActive || login.LockoutEnd > now)
        {
            Hasher.VerifyHashedPassword(login, DummyHash, password ?? string.Empty);
            return null;
        }

        if (Hasher.VerifyHashedPassword(login, login.HashedPassword, password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            await CountFailureAsync(login.Id, now, cancellationToken);
            return null;
        }

        await db.CustomerLogins
            .Where(c => c.Id == login.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AccessFailedCount, 0).SetProperty(c => c.UpdatedAt, now), cancellationToken);
        return login;
    }

    /// <summary>A wrong second-factor code counts like a wrong password.</summary>
    public async Task CountFailureAsync(Guid subjectId, CancellationToken cancellationToken)
    {
        var id = await db.CustomerLogins.Where(c => c.SubjectId == subjectId).Select(c => c.Id).SingleOrDefaultAsync(cancellationToken);
        if (id != 0)
            await CountFailureAsync(id, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>Whether the sign-in is locked now (after the last counted failure).</summary>
    public Task<bool> IsLockedAsync(Guid subjectId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return db.CustomerLogins.AnyAsync(c => c.SubjectId == subjectId && c.LockoutEnd > now, cancellationToken);
    }

    /// <summary>Creates the sign-in at the end of a registration; false when this customer already has one.</summary>
    public async Task<bool> CreateAsync(CustomerLogin login, string password, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        login.HashedPassword = Hasher.HashPassword(login, password);
        login.IsActive = true;
        login.CreatedAt = now;
        login.UpdatedAt = now;
        db.CustomerLogins.Add(login);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique customer number: a registration for the same customer finished first.
            db.Entry(login).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Undoes a registration whose link at Customer Onboarding was refused.</summary>
    public Task RemoveAsync(Guid subjectId, CancellationToken cancellationToken) =>
        db.CustomerLogins.Where(c => c.SubjectId == subjectId).ExecuteDeleteAsync(cancellationToken);

    private Task<int> CountFailureAsync(long id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset? lockoutEnd = now.Add(LockoutDuration);
        return db.CustomerLogins
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.LockoutEnd, c => c.AccessFailedCount + 1 >= MaxFailedAttempts ? lockoutEnd : c.LockoutEnd)
                .SetProperty(c => c.AccessFailedCount, c => c.AccessFailedCount + 1 >= MaxFailedAttempts ? 0 : c.AccessFailedCount + 1)
                .SetProperty(c => c.UpdatedAt, now), cancellationToken);
    }
}