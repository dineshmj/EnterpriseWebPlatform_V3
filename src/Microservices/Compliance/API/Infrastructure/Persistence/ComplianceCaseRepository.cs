using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Application.Queries;
using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Compliance.Api.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.Compliance.Api.Infrastructure.Persistence;

// ┌─ :: ARCHITECTURE :: Row locks for actions and screening ───────────────────────────────────────
// │ Why : Two officers, or two screening instances, working on the same case at once could each
// │       miss the other's change.
// │ How : Officer actions lock the case FOR UPDATE; the screening worker takes due cases FOR UPDATE
// │       SKIP LOCKED; Version is the second guard at save time. Every case loaded for a change is
// │       decrypted (ComplianceCasePersonalData); an erased case is never due for screening.
// │ Ref : Features 1.6.4 | Pessimistic + optimistic concurrency
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ComplianceCaseRepository(ComplianceDbContext db, ComplianceCasePersonalData personalData) : IComplianceCaseRepository
{
    public async Task<ComplianceCase?> GetByApplicationRefAsync(Guid applicationRef, CancellationToken cancellationToken) =>
        await RevealAsync(
            await db.ComplianceCases.SingleOrDefaultAsync(x => x.ApplicationRef == applicationRef, cancellationToken),
            cancellationToken);

    public async Task<ComplianceCase?> GetForUpdateAsync(long caseId, CancellationToken cancellationToken)
    {
        // Lock the row first (concurrent officers queue here; the version column is the
        // second line of defence), then load the aggregate with a normal query.
        var locked = await db.Database
            .SqlQuery<long>($"SELECT id AS \"Value\" FROM compliance_cases WHERE id = {caseId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return locked.Count == 0
            ? null
            : await RevealAsync(await db.ComplianceCases.SingleAsync(x => x.Id == caseId, cancellationToken), cancellationToken);
    }

    public async Task<ComplianceCase?> GetNextDueForScreeningAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // SKIP LOCKED: several API instances screen in parallel without ever taking the same case.
        var ids = await db.Database
            .SqlQuery<long>($"""
                SELECT id AS "Value"
                FROM compliance_cases
                WHERE status = 'SCREENING' AND next_screening_at <= {now} AND personal_data_erased_at IS NULL
                ORDER BY next_screening_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        return ids.Count == 0
            ? null
            : await RevealAsync(await db.ComplianceCases.SingleAsync(x => x.Id == ids[0], cancellationToken), cancellationToken);
    }

    // Not decrypted: the only caller erases them.
    public async Task<IReadOnlyList<ComplianceCase>> ListForErasureAsync(string customerNumber, CancellationToken cancellationToken) =>
        await db.ComplianceCases.Where(x => x.CustomerNumber == customerNumber).OrderBy(x => x.Id).ToListAsync(cancellationToken);

    public void Add(ComplianceCase complianceCase) => db.ComplianceCases.Add(complianceCase);

    private async Task<ComplianceCase?> RevealAsync(ComplianceCase? complianceCase, CancellationToken cancellationToken)
    {
        if (complianceCase is not null)
            await personalData.RevealAsync(complianceCase, cancellationToken);
        return complianceCase;
    }
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The applicant is stored encrypted: each query reads its page with the ciphertext, then
// │ decrypts only those rows (ComplianceCasePersonalData.ShowAsync). An erased case reads
// │ "Personal data erased" with no address.
// └──────────────────────────────────────────────────────────────────────────────────────
public sealed class ComplianceCaseQueries(ComplianceDbContext db, ComplianceCasePersonalData personalData) : IComplianceCaseQueries
{
    public async Task<PagedComplianceCasesResponse> GetCasesAsync(
        BranchCode branch, int pageNumber, int pageSize, ComplianceCaseStatus? status, CancellationToken cancellationToken)
    {
        var query = InBranch(branch);
        if (status is { } s)
            query = query.Where(x => x.Status == s);

        // ARCH: a status tab is a work queue, so oldest first (first in, first decided). "All" (no status)
        //       is a lookup list, so newest first - the case just raised is on page 1, not past the last page.
        var ordered = status is null
            ? query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            : query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);

        var total = await query.CountAsync(cancellationToken);
        var page = await Project(ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);

        return new PagedComplianceCasesResponse(await ShowAsync(page, cancellationToken), pageNumber, pageSize, total);
    }

    public async Task<ComplianceCaseDetail?> GetCaseAsync(long caseId, BranchCode branch, CancellationToken cancellationToken)
    {
        var row = await Project(InBranch(branch).Where(x => x.Id == caseId)).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (await ShowAsync([row], cancellationToken))[0];
    }

    // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
    // │ BranchCode is stored through a value converter: compare against a BranchCode value.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private IQueryable<ComplianceCase> InBranch(BranchCode branch) =>
        db.ComplianceCases.AsNoTracking().Where(x => x.BranchCode == branch);

    private async Task<IReadOnlyList<ComplianceCaseDetail>> ShowAsync(IReadOnlyList<Row> rows, CancellationToken cancellationToken)
    {
        var shown = await personalData.ShowAsync(
            rows.Select(x => (x.Detail.CustomerNumber, x.Applicant, x.Detail.PersonalDataErased)).ToList(),
            cancellationToken);

        return rows
            .Select((x, i) => x.Detail with { CustomerName = shown[i].Name, ResidentialAddress = shown[i].Address })
            .ToList();
    }

    private IQueryable<Row> Project(IQueryable<ComplianceCase> query) =>
        query.Select(x => new Row(
            EF.Property<string>(x, ComplianceCasePersonalData.ApplicantColumn),
            new ComplianceCaseDetail(
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
                x.UpdatedAt,
                string.Empty,   // decrypted after the query
                new ApplicantAddress(null, null, null, null, null, null),   // decrypted after the query
                new StaffLanIds(
                    db.StaffMembers.Where(s => s.UserId == x.InitiatedByUserId).Select(s => s.LanId).FirstOrDefault(),
                    db.StaffMembers.Where(s => s.UserId == x.KycIdentityDecidedByUserId).Select(s => s.LanId).FirstOrDefault(),
                    db.StaffMembers.Where(s => s.UserId == x.KycDocumentDecidedByUserId).Select(s => s.LanId).FirstOrDefault(),
                    db.StaffMembers.Where(s => s.UserId == x.AssignedOfficerUserId).Select(s => s.LanId).FirstOrDefault(),
                    db.StaffMembers.Where(s => s.UserId == x.DecisionByUserId).Select(s => s.LanId).FirstOrDefault()),
                x.PersonalDataErasedAt != null)));

    private sealed record Row(string Applicant, ComplianceCaseDetail Detail);
}