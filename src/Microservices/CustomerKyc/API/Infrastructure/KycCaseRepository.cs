using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

public sealed class KycCaseRepository(KycDbContext db) : IKycCaseRepository
{
    public Task<KycCase?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken) =>
        db.KycCases.SingleOrDefaultAsync(x => x.ApplicationRef == applicationRef, cancellationToken);

    public async Task<KycCase?> GetForDecisionAsync(long caseId, CancellationToken cancellationToken)
    {
        // 1. Lock the row (PostgreSQL holds it until the transaction ends); concurrent
        //    deciders queue here. The version column is the second line of defence.
        // 2. Load the aggregate with a normal query. Under READ COMMITTED this new
        //    statement sees everything committed before the lock was granted.
        //    (Loading via FromSql is avoided: EF Core maps complex-type columns of a
        //    FromSql result by their default names, not the configured ones.)
        var locked = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM kyc_cases WHERE id = {caseId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        if (locked.Count == 0)
            return null;

        return await db.KycCases.SingleOrDefaultAsync(x => x.Id == caseId, cancellationToken);
    }

    public void Add(KycCase kycCase) => db.KycCases.Add(kycCase);
}