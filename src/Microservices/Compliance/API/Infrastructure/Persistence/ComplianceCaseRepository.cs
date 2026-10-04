using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Application.Queries;
using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;

public sealed class ComplianceCaseRepository(ComplianceDbContext db) : IComplianceCaseRepository
{
    public Task<ComplianceCase?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken) =>
        db.ComplianceCases.SingleOrDefaultAsync(x => x.ApplicationRef == applicationRef, cancellationToken);

    public async Task<ComplianceCase?> GetForUpdateAsync(long caseId, CancellationToken cancellationToken)
    {
        // Lock the row first (concurrent officers queue here; the version column is the
        // second line of defence), then load the aggregate with a normal query.
        var locked = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM compliance_cases WHERE id = {caseId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return locked.Count == 0 ? null : await db.ComplianceCases.SingleAsync(x => x.Id == caseId, cancellationToken);
    }

    public async Task<ComplianceCase?> GetNextDueForScreeningAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // SKIP LOCKED: several API instances screen in parallel without ever taking the same case.
        var ids = await db.Database
            .SqlQuery<long>($"""
                SELECT id AS "Value"
                FROM compliance_cases
                WHERE status = 'SCREENING' AND next_screening_at <= {now}
                ORDER BY next_screening_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        return ids.Count == 0 ? null : await db.ComplianceCases.SingleAsync(x => x.Id == ids[0], cancellationToken);
    }

    public void Add(ComplianceCase complianceCase) => db.ComplianceCases.Add(complianceCase);
}

public sealed class ComplianceCaseQueries(ComplianceDbContext db) : IComplianceCaseQueries
{
    public async Task<PagedComplianceCasesResponse> GetCasesAsync(
        BranchCode branch, int pageNumber, int pageSize, ComplianceCaseStatus? status, CancellationToken cancellationToken)
    {
        var query = InBranch(branch);
        if (status is { } s)
            query = query.Where(x => x.Status == s);

        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query.OrderBy(x => x.CreatedAt).Skip((pageNumber - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);

        return new PagedComplianceCasesResponse(items, pageNumber, pageSize, total);
    }

    public Task<ComplianceCaseDetail?> GetCaseAsync(long caseId, BranchCode branch, CancellationToken cancellationToken) =>
        Project(InBranch(branch).Where(x => x.Id == caseId)).SingleOrDefaultAsync(cancellationToken);

    // BranchCode is stored through a value converter: compare against a BranchCode value.
    private IQueryable<ComplianceCase> InBranch(BranchCode branch) =>
        db.ComplianceCases.AsNoTracking().Where(x => x.BranchCode == branch);

    private static IQueryable<ComplianceCaseDetail> Project(IQueryable<ComplianceCase> query) =>
        query.Select(x => new ComplianceCaseDetail(
            x.Id,
            x.ApplicationNumber,
            x.CustomerNumber,
            x.KycCaseId,
            x.BranchCode.Value,
            x.Status.ToCode(),
            x.ScreeningOutcome == null ? null : x.ScreeningOutcome.Value.ToCode(),
            x.ScreeningProvider,
            x.ScreeningReference,
            x.ScreenedAt,
            x.ScreeningAttempts,
            x.NextScreeningAt,
            x.LastScreeningError,
            x.RiskRating == null ? null : x.RiskRating.Value.ToCode(),
            x.RequiredClearance,
            x.InitiatedByUserId,
            x.KycIdentityDecidedByUserId,
            x.KycDocumentDecidedByUserId,
            x.AssignedOfficerUserId,
            x.HoldReason,
            x.DecisionByUserId,
            x.DecisionAt,
            x.DecisionRemarks,
            x.CreatedAt,
            x.UpdatedAt));
}