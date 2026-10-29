using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.IdentityServer.Data;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Security;

/// <summary>Configuration "Mfa": switched off by default; when on, it applies to every user.</summary>
public sealed class MfaOptions
{
    public const string SectionName = "Mfa";

    public bool Enabled { get; init; }

    /// <summary>How the account is labelled in the authenticator app ("EWP V3 Demo: emmar").</summary>
    public string Issuer { get; init; } = "EWP V3 Demo";
}

// ┌─ :: SECURITY :: Authenticator secrets and codes ───────────────────────────────────────────────
// │ Why : The TOTP secret is as sensitive as a password, and an intercepted code must not be
// │       reusable.
// │ How : Secrets encrypted with the IDP's Data Protection key ring; recovery codes stored only as
// │       hashes; a code is accepted only for a time step later than the last one used (atomic
// │       update); wrong codes count towards the lockout.
// │ Ref : Features 2.1.3 | RFC 6238 | ASVS V2.8.4 (no reuse), V6.4 |
// │       AI review 1 fix R2 (atomic claim)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Enrolment and verification of a user's authenticator. The secret is encrypted with the IDP's
/// key ring before it reaches the database; recovery codes are stored as hashes only. A wrong
/// code counts towards the same lockout as a wrong password (5 attempts, 15 minutes).
/// </summary>
public sealed class MfaService(IdentityDbContext db, IDataProtectionProvider dataProtection, TimeProvider time)
{
    private const int RecoveryCodeCount = 10;
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IDataProtector _protector = dataProtection.CreateProtector("EnterpriseWebPlatform.IdentityServer.Mfa.Secret.v1");

    public Task<bool> IsEnrolledAsync(long userId, CancellationToken cancellationToken) =>
        db.UserMfa.AnyAsync(m => m.UserId == userId, cancellationToken);

    /// <summary>
    /// Confirms an enrolment with the first code from the app; saves the (encrypted) secret and
    /// returns the recovery codes - shown once, never stored readable. Null when the code is wrong
    /// (which counts towards the lockout, like a wrong code at sign-in) or the user is locked out.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ConfirmEnrolmentAsync(User user, byte[] secret, string code, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (user.LockoutEnd > now)
            return null;

        var step = Totp.MatchingTimeStep(secret, code.Trim(), now);
        if (step is null)
        {
            await CountFailureAsync(user.Id, now, cancellationToken);
            return null;
        }

        var userId = user.Id;
        db.UserMfaRecoveryCodes.RemoveRange(db.UserMfaRecoveryCodes.Where(c => c.UserId == userId));
        var existing = await db.UserMfa.FindAsync([userId], cancellationToken);
        if (existing is not null)
            db.UserMfa.Remove(existing);

        db.UserMfa.Add(new UserMfa
        {
            UserId = userId,
            SecretProtected = _protector.Protect(Convert.ToBase64String(secret)),
            EnrolledAt = now,
            LastUsedTimeStep = step.Value
        });

        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        db.UserMfaRecoveryCodes.AddRange(codes.Select(c => new UserMfaRecoveryCode { UserId = userId, CodeHash = HashOf(c) }));
        await db.SaveChangesAsync(cancellationToken);
        return codes;
    }

    /// <summary>
    /// Checks a code from the app (or an unused recovery code) for an enrolled, unlocked user.
    /// A code is accepted once only - also when two requests carry it at the same moment: each
    /// one-time value is claimed with ONE conditional UPDATE, and only the request whose UPDATE
    /// changed the row wins. A wrong code counts towards the lockout.
    /// </summary>
    public async Task<bool> VerifyAsync(User user, string input, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (user.LockoutEnd > now)
            return false;

        var mfa = await db.UserMfa.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == user.Id, cancellationToken);
        if (mfa is null)
            return false;

        var code = input.Replace(" ", string.Empty).Replace("-", string.Empty).Trim();
        var secret = Convert.FromBase64String(_protector.Unprotect(mfa.SecretProtected));
        var accepted = false;

        if (Totp.MatchingTimeStep(secret, code, now) is { } step)
        {
            // The time step moves forward only: the same code (or an older one) never counts twice.
            accepted = await db.UserMfa
                .Where(m => m.UserId == user.Id && m.LastUsedTimeStep < step)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedTimeStep, step), cancellationToken) == 1;
        }
        else if (code.Length == 10)
        {
            var hash = HashOf(code.ToUpperInvariant());
            accepted = await db.UserMfaRecoveryCodes
                .Where(c => c.UserId == user.Id && c.CodeHash == hash && c.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), cancellationToken) == 1;
        }

        if (accepted)
        {
            await db.Users
                .Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, 0).SetProperty(u => u.UpdatedAt, now), cancellationToken);
        }
        else
        {
            await CountFailureAsync(user.Id, now, cancellationToken);
        }

        return accepted;
    }

    /// <summary>
    /// One more wrong code, counted in ONE UPDATE (concurrent wrong codes cannot under-count): the
    /// fifth locks the account for 15 minutes and starts the count again. Every SET reads the
    /// row's values from before the UPDATE.
    /// </summary>
    private Task<int> CountFailureAsync(long userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DateTimeOffset? lockoutEnd = now.Add(LockoutDuration);
        return db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LockoutEnd, u => u.AccessFailedCount + 1 >= MaxFailedAttempts ? lockoutEnd : u.LockoutEnd)
                .SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1 >= MaxFailedAttempts ? 0 : u.AccessFailedCount + 1)
                .SetProperty(u => u.UpdatedAt, now), cancellationToken);
    }

    /// <summary>Ten characters from an alphabet without look-alikes (no 0/O, 1/I/L), shown as XXXXX-XXXXX.</summary>
    private static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return string.Concat(Enumerable.Range(0, 10).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }

    public static string Display(string recoveryCode) => $"{recoveryCode[..5]}-{recoveryCode[5..]}";

    private static string HashOf(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}