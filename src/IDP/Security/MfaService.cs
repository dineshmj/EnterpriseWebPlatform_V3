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
    /// returns the recovery codes - shown once, never stored readable. Null when the code is wrong.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ConfirmEnrolmentAsync(long userId, byte[] secret, string code, CancellationToken cancellationToken)
    {
        var step = Totp.MatchingTimeStep(secret, code.Trim(), time.GetUtcNow());
        if (step is null)
            return null;

        db.UserMfaRecoveryCodes.RemoveRange(db.UserMfaRecoveryCodes.Where(c => c.UserId == userId));
        var existing = await db.UserMfa.FindAsync([userId], cancellationToken);
        if (existing is not null)
            db.UserMfa.Remove(existing);

        db.UserMfa.Add(new UserMfa
        {
            UserId = userId,
            SecretProtected = _protector.Protect(Convert.ToBase64String(secret)),
            EnrolledAt = time.GetUtcNow(),
            LastUsedTimeStep = step.Value
        });

        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        db.UserMfaRecoveryCodes.AddRange(codes.Select(c => new UserMfaRecoveryCode { UserId = userId, CodeHash = HashOf(c) }));
        await db.SaveChangesAsync(cancellationToken);
        return codes;
    }

    /// <summary>
    /// Checks a code from the app (or an unused recovery code) for an enrolled, unlocked user.
    /// A code is accepted once only; a wrong one counts towards the lockout.
    /// </summary>
    public async Task<bool> VerifyAsync(User user, string input, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (user.LockoutEnd > now)
            return false;

        var mfa = await db.UserMfa.SingleOrDefaultAsync(m => m.UserId == user.Id, cancellationToken);
        if (mfa is null)
            return false;

        var code = input.Replace(" ", string.Empty).Replace("-", string.Empty).Trim();
        var accepted = false;

        var secret = Convert.FromBase64String(_protector.Unprotect(mfa.SecretProtected));
        if (Totp.MatchingTimeStep(secret, code, now) is { } step && step > mfa.LastUsedTimeStep)
        {
            mfa.LastUsedTimeStep = step;   // the same code can never be replayed
            accepted = true;
        }
        else if (code.Length == 10)
        {
            var hash = HashOf(code.ToUpperInvariant());
            var recovery = await db.UserMfaRecoveryCodes
                .SingleOrDefaultAsync(c => c.UserId == user.Id && c.CodeHash == hash && c.UsedAt == null, cancellationToken);
            if (recovery is not null)
            {
                recovery.UsedAt = now;
                accepted = true;
            }
        }

        if (accepted)
        {
            user.AccessFailedCount = 0;
        }
        else if (++user.AccessFailedCount >= MaxFailedAttempts)
        {
            user.LockoutEnd = now.Add(LockoutDuration);
            user.AccessFailedCount = 0;
        }

        user.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return accepted;
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