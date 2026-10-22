using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.IdentityServer.Data;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Customers;

public enum CodeCheck
{
    Accepted,
    Wrong,
    /// <summary>Expired, already used, or out of tries: a new code is needed.</summary>
    Spent
}

// ┌─ :: SECURITY :: One-time codes for customers ──────────────────────────────────────────────────
// │ Why : The code proves the person holds the mobile on record (registration) or is the second
// │       factor (sign-in, MFA on). Six digits are guessable unless tries are few, the code is
// │       short-lived and each code works once.
// │ How : Six random digits, five minutes, five tries; stored only as a salted SHA-256 hash; the
// │       row is claimed with ONE conditional UPDATE (not used, not expired, tries left, right hash),
// │       so two requests carrying the same code cannot both pass; a wrong code is counted on the
// │       row, never in the browser (a replayed cookie gets no fresh tries).
// │ Ref : Features 2.1.6 | NIST SP 800-63B §5.1.3 (out-of-band, RESTRICTED - production: an app
// │       or passkey, B4) | ASVS V2.7 | Threat-Model R-17
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Issues and checks the customers' one-time codes.</summary>
public sealed class CustomerOneTimeCodes(IdentityDbContext db, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxFailedAttempts = 5;

    /// <summary>A new code; returns the row's ID (for the browser's cookie) and the code (for the SMS).</summary>
    public async Task<(Guid Id, string Code)> IssueAsync(CustomerOneTimeCode template, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        // Housekeeping: codes are useless an hour after they expired.
        var stale = now.AddHours(-1);
        await db.CustomerOneTimeCodes.Where(c => c.ExpiresAt < stale).ExecuteDeleteAsync(cancellationToken);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        template.Id = Guid.NewGuid();
        template.Salt = salt;
        template.CodeHash = HashOf(salt, code);
        template.FailedAttempts = 0;
        template.CreatedAt = now;
        template.ExpiresAt = now.Add(Lifetime);
        template.UsedAt = null;

        db.CustomerOneTimeCodes.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return (template.Id, code);
    }

    /// <summary>The code's row while it can still be entered (or null).</summary>
    public Task<CustomerOneTimeCode?> FindOpenAsync(Guid id, CustomerCodePurpose purpose, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return db.CustomerOneTimeCodes.AsNoTracking().SingleOrDefaultAsync(
            c => c.Id == id && c.Purpose == purpose && c.UsedAt == null && c.ExpiresAt > now && c.FailedAttempts < MaxFailedAttempts,
            cancellationToken);
    }

    /// <summary>Checks a typed code against the row; accepted at most once.</summary>
    public async Task<CodeCheck> CheckAsync(CustomerOneTimeCode open, string? typed, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var hash = HashOf(open.Salt, (typed ?? string.Empty).Trim());

        var claimed = await db.CustomerOneTimeCodes
            .Where(c => c.Id == open.Id && c.UsedAt == null && c.ExpiresAt > now && c.FailedAttempts < MaxFailedAttempts && c.CodeHash == hash)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), cancellationToken);
        if (claimed == 1)
            return CodeCheck.Accepted;

        var counted = await db.CustomerOneTimeCodes
            .Where(c => c.Id == open.Id && c.UsedAt == null && c.ExpiresAt > now && c.FailedAttempts < MaxFailedAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedAttempts, c => c.FailedAttempts + 1), cancellationToken);
        if (counted == 0)
            return CodeCheck.Spent;

        return await FindOpenAsync(open.Id, open.Purpose, cancellationToken) is null ? CodeCheck.Spent : CodeCheck.Wrong;
    }

    // Constant-time comparison is not needed: the hash is compared inside the database, and a
    // six-digit code has no prefix worth timing - tries, not timing, are the limit.
    private static string HashOf(string salt, string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{code}")));
}