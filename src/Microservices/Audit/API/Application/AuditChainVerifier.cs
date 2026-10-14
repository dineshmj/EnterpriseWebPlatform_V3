using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Audit.Api.Domain;
using EnterpriseWebPlatform.Audit.Api.Infrastructure;

namespace EnterpriseWebPlatform.Audit.Api.Application;

/// <summary>The outcome of re-walking the chain (and comparing it with the checkpoints kept outside the database).</summary>
public sealed record AuditChainVerification(
    bool Intact,
    long EntriesChecked,
    long? BrokenAtSequence,
    string? Problem,
    DateTimeOffset VerifiedAt,
    long CheckpointsChecked = 0,
    long LastSequence = 0,
    string? LastEntryHash = null);

// ┌─ :: SECURITY :: Verification: chain, anchors, truncation ──────────────────────────────────────
// │ Why : Tampering is only useful to detect if someone checks.
// │ How : Every hash and link is recomputed; every anchor must carry a valid signature and still
// │       match the chain; a chain shorter than its newest anchor means entries were removed.
// │ Ref : Features 2.4.8 | OWASP A09
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Re-walks the whole trail in sequence order and checks, for every entry: the sequence has no
/// gap, it points at the previous entry's hash, and its own hash still matches its content - an
/// edited, deleted, inserted or reordered entry cannot pass.
///
/// Then the anchors (signed checkpoints kept OUTSIDE the database): each must carry a valid
/// signature, the chain must still hold the anchored hash at the anchored position, and must not
/// end before the latest checkpoint. That also catches the one attack the chain alone cannot: a
/// privileged administrator rewriting the whole trail and recomputing every hash.
/// </summary>
public sealed class AuditChainVerifier(AuditDbContext db, AuditAnchorStore anchors, AuditCheckpointSigner signer, TimeProvider time)
{
    private const int BatchSize = 1000;

    public async Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        var checkpoints = await anchors.ReadAllAsync(cancellationToken);
        if (checkpoints.FirstOrDefault(c => !signer.IsValid(c)) is { } forged)
        {
            return Broken(0, forged.Sequence,
                $"the checkpoint for entry {forged.Sequence} kept outside the database has no valid signature (the anchor itself was altered)");
        }
        var anchored = checkpoints.ToDictionary(c => c.Sequence, c => c.EntryHash);

        long expectedSequence = 1;
        var previousHash = AuditChain.Genesis;
        long checkedCount = 0;
        long checkpointsChecked = 0;

        while (true)
        {
            var batch = await db.Entries.AsNoTracking()
                .Where(e => e.Sequence >= expectedSequence)
                .OrderBy(e => e.Sequence)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var entry in batch)
            {
                string? problem =
                    entry.Sequence != expectedSequence ? $"entry {expectedSequence} is missing (next is {entry.Sequence})"
                    : entry.PreviousHash != previousHash ? "it does not point at the previous entry's hash"
                    : entry.EntryHash != AuditChain.ComputeHash(entry) ? "its content no longer matches its hash"
                    : anchored.TryGetValue(entry.Sequence, out var anchoredHash) && anchoredHash != entry.EntryHash
                        ? "it no longer matches the signed checkpoint kept outside the database (the trail was rewritten)"
                    : null;

                if (problem is not null)
                    return Broken(checkedCount, expectedSequence, problem, checkpointsChecked);

                if (anchored.ContainsKey(entry.Sequence))
                    checkpointsChecked++;
                previousHash = entry.EntryHash;
                expectedSequence++;
                checkedCount++;
            }

            if (batch.Count < BatchSize)
                break;
        }

        var lastSequence = expectedSequence - 1;
        var latestAnchored = anchored.Keys.DefaultIfEmpty(0).Max();
        // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        // │ If an anchor names an entry beyond the end of the trail, entries were removed
        // │ (truncation): report it as broken.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (latestAnchored > lastSequence)
        {
            return Broken(checkedCount, expectedSequence,
                $"entries up to {latestAnchored} were anchored outside the database, but the trail ends at {lastSequence} (entries were removed)", checkpointsChecked);
        }

        return new AuditChainVerification(true, checkedCount, null, null, time.GetUtcNow(), checkpointsChecked, lastSequence,
            lastSequence > 0 ? previousHash : null);
    }

    private AuditChainVerification Broken(long checkedCount, long atSequence, string problem, long checkpointsChecked = 0) =>
        new(false, checkedCount, atSequence, problem, time.GetUtcNow(), checkpointsChecked);
}