using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Audit.Api.Domain;

namespace EnterpriseWebPlatform.Audit.Api.Infrastructure;

public enum AppendResult { Appended, Duplicate }

// ┌─ :: ARCHITECTURE + SECURITY :: One writer at a time, each event once ──────────────────────────
// │ Why : Two instances appending in parallel would fork the chain; a redelivered event would be
// │       recorded twice.
// │ How : A transaction-scoped advisory lock serialises appends; a known message ID is skipped
// │       (Duplicate); the new entry links to the current tail.
// │ Ref : Features 1.3.2, 2.4.8 | Idempotent Consumer pattern
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Appends one entry to the chain. The chain has a single tail, so appends are serialised
/// with a PostgreSQL advisory lock held for the transaction: even with several instances,
/// each entry gets the next sequence number and the hash of the entry just before it.
/// A message already recorded (same MessageId) is a duplicate and changes nothing.
/// </summary>
public sealed class AuditTrailAppender(AuditDbContext db)
{
    // Any constant: the lock's name. Only this service takes it.
    private const long ChainLockKey = 7_151_001;

    public async Task<AppendResult> AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // ┌─ DEBT ───────────────────────────────────────────────────────────────────────────────
        // │ One global chain: every append waits for this lock, so audit writes are serialised.
        // │ Accepted for the PoC; a benchmark under realistic load decides whether partitioned
        // │ chains are needed (production item 14).
        // └──────────────────────────────────────────────────────────────────────────────────────
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ChainLockKey})", cancellationToken);

        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ If this message is already in the trail, skip it - a redelivery never adds a second
        // │ entry.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (await db.Entries.AnyAsync(e => e.MessageId == entry.MessageId, cancellationToken))
            return AppendResult.Duplicate;

        var tail = await db.Entries.AsNoTracking()
            .OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.EntryHash })
            .FirstOrDefaultAsync(cancellationToken);

        entry.Sequence = (tail?.Sequence ?? 0) + 1;
        entry.PreviousHash = tail?.EntryHash ?? AuditChain.Genesis;
        entry.EntryHash = AuditChain.ComputeHash(entry);

        db.Entries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AppendResult.Appended;
    }
}