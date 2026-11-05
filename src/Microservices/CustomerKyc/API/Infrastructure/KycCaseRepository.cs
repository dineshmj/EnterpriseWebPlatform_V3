using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

// ┌─ :: ARCHITECTURE :: Row lock for decisions ────────────────────────────────────────────────────
// │ Why : Two officers deciding the two stages of one case at the same moment could each miss the
// │       other's decision.
// │ How : Decisions load the case with SELECT ... FOR UPDATE inside the transaction, so they queue;
// │       Version is the second guard at save time.
// │ Ref : Features 1.6.4 | Pessimistic + optimistic concurrency
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class KycCaseRepository(KycDbContext db, KycCasePersonalData personalData) : IKycCaseRepository
{
    public async Task<KycCase?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken) =>
        await RevealAsync(
            await db.KycCases.SingleOrDefaultAsync(x => x.ApplicationRef == applicationRef, cancellationToken),
            cancellationToken);

    // Not decrypted: the only caller erases them.
    public async Task<IReadOnlyList<KycCase>> ListForErasureAsync(string customerNumber, CancellationToken cancellationToken) =>
        await db.KycCases.Where(x => x.CustomerNumber == customerNumber).OrderBy(x => x.Id).ToListAsync(cancellationToken);

    public async Task<KycCase?> GetForDecisionAsync(long caseId, CancellationToken cancellationToken)
    {
        // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
        // │ 1. Lock the row (PostgreSQL holds it until the transaction ends); concurrent
        // │    deciders queue here. The version column is the second line of defence.
        // │ 2. Load the aggregate with a normal query. Under READ COMMITTED this new
        // │    statement sees everything committed before the lock was granted.
        // │    (Loading via FromSql is avoided: EF Core maps complex-type columns of a
        // │    FromSql result by their default names, not the configured ones.)
        // └──────────────────────────────────────────────────────────────────────────────────────
        var locked = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM kyc_cases WHERE id = {caseId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        if (locked.Count == 0)
            return null;

        return await RevealAsync(
            await db.KycCases.SingleOrDefaultAsync(x => x.Id == caseId, cancellationToken),
            cancellationToken);
    }

    public void Add(KycCase kycCase) => db.KycCases.Add(kycCase);

    private async Task<KycCase?> RevealAsync(KycCase? kycCase, CancellationToken cancellationToken)
    {
        if (kycCase is not null)
            await personalData.RevealAsync(kycCase, cancellationToken);
        return kycCase;
    }
}