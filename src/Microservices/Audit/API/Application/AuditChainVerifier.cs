using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Audit.Api.Domain;
using EnterpriseWebPlatform.Audit.Api.Infrastructure;

namespace EnterpriseWebPlatform.Audit.Api.Application;

/// <summary>The outcome of re-walking the chain.</summary>
public sealed record AuditChainVerification(
    bool Intact,
    long EntriesChecked,
    long? BrokenAtSequence,
    string? Problem,
    DateTimeOffset VerifiedAt);

/// <summary>
/// Re-walks the whole trail in sequence order and checks three things for every entry:
/// the sequence has no gap, it points at the previous entry's hash, and its own hash still
/// matches its content. The first entry that fails is reported - an edited, deleted,
/// inserted or reordered entry cannot pass.
/// </summary>
public sealed class AuditChainVerifier(AuditDbContext db, TimeProvider time)
{
    private const int BatchSize = 1000;

    public async Task<AuditChainVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        long expectedSequence = 1;
        var previousHash = AuditChain.Genesis;
        long checkedCount = 0;

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
                    : null;

                if (problem is not null)
                    return new AuditChainVerification(false, checkedCount, expectedSequence, problem, time.GetUtcNow());

                previousHash = entry.EntryHash;
                expectedSequence++;
                checkedCount++;
            }

            if (batch.Count < BatchSize)
                return new AuditChainVerification(true, checkedCount, null, null, time.GetUtcNow());
        }
    }
}