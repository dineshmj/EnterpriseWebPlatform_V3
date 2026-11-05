using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Queries;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The applicant is stored encrypted: each query reads its page with the ciphertext, then
// │ decrypts only those rows (KycCasePersonalData.ShowAsync). An erased case reads
// │ "Personal data erased" with no address.
// └──────────────────────────────────────────────────────────────────────────────────────
public sealed class KycCaseQueries(KycDbContext db, KycCasePersonalData personalData) : IKycCaseQueries
{
    public async Task<PagedKycCasesResponse> GetCasesAsync(
        BranchCode branch,
        int pageNumber,
        int pageSize,
        KycCaseStatus? status,
        VerificationStageType? pendingStage,
        CancellationToken cancellationToken)
    {
        var query = InBranch(branch);

        if (status is { } s)
            query = query.Where(x => x.Status == s);

        if (pendingStage == VerificationStageType.IdentityVerification)
            query = query.Where(x => x.IdentityVerification.Status == VerificationStatus.PendingReview);
        else if (pendingStage == VerificationStageType.DocumentVerification)
            query = query.Where(x => x.DocumentVerification.Status == VerificationStatus.PendingReview);

        var totalCount = await query.CountAsync(cancellationToken);

        // ARCH: a work queue, so oldest first (first in, first decided); ties broken by Id so the
        //       pages never overlap or skip a case.
        var page = await query
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                Applicant = EF.Property<string>(x, KycCasePersonalData.ApplicantColumn),
                Item = new KycCaseListItem(
                    x.Id,
                    x.CustomerNumber,
                    x.ApplicationNumber,
                    x.Status.ToCode(),
                    x.IdentityVerification.Status.ToCode(),
                    x.IdentityVerification.DecidedByUserId,
                    x.IdentityVerification.DecidedAt,
                    x.IdentityVerification.Remarks,
                    x.DocumentVerification.Status.ToCode(),
                    x.DocumentVerification.DecidedByUserId,
                    x.DocumentVerification.DecidedAt,
                    x.DocumentVerification.Remarks,
                    x.InitiatedByUserId,
                    x.DecisionByUserId,
                    x.DecisionAt,
                    x.DecisionRemarks,
                    x.CreatedAt,
                    x.UpdatedAt,
                    x.BranchCode.Value,
                    x.AssignedOfficerUserId,
                    string.Empty,
                    new ApplicantAddress(null, null, null, null, null, null),   // decrypted after the query
                    new StaffLanIds(
                        db.StaffMembers.Where(s => s.UserId == x.InitiatedByUserId).Select(s => s.LanId).FirstOrDefault(),
                        db.StaffMembers.Where(s => s.UserId == x.AssignedOfficerUserId).Select(s => s.LanId).FirstOrDefault(),
                        db.StaffMembers.Where(s => s.UserId == x.IdentityVerification.DecidedByUserId).Select(s => s.LanId).FirstOrDefault(),
                        db.StaffMembers.Where(s => s.UserId == x.DocumentVerification.DecidedByUserId).Select(s => s.LanId).FirstOrDefault(),
                        db.StaffMembers.Where(s => s.UserId == x.DecisionByUserId).Select(s => s.LanId).FirstOrDefault()),
                    x.PersonalDataErasedAt != null)
            })
            .ToListAsync(cancellationToken);

        var shown = await personalData.ShowAsync(
            page.Select(x => (x.Item.CustomerNumber, x.Applicant, x.Item.PersonalDataErased)).ToList(),
            cancellationToken);

        var items = page
            .Select((x, i) => x.Item with { CustomerName = shown[i].Name, ResidentialAddress = shown[i].Address })
            .ToList();

        return new PagedKycCasesResponse(items, pageNumber, pageSize, totalCount);
    }

    public async Task<KycCaseDetail?> GetCaseAsync(long caseId, BranchCode branch, CancellationToken cancellationToken)
    {
        var row = await InBranch(branch)
            .Where(x => x.Id == caseId)
            .Select(x => new
            {
                Applicant = EF.Property<string>(x, KycCasePersonalData.ApplicantColumn),
                Detail = new KycCaseDetail(
                    x.Id,
                    x.CustomerNumber,
                    x.ApplicationNumber,
                    x.Status.ToCode(),
                    x.IdentityVerification.Status.ToCode(),
                    x.IdentityVerification.DecidedByUserId,
                    x.IdentityVerification.DecidedAt,
                    x.IdentityVerification.Remarks,
                    x.DocumentVerification.Status.ToCode(),
                    x.DocumentVerification.DecidedByUserId,
                    x.DocumentVerification.DecidedAt,
                    x.DocumentVerification.Remarks,
                    x.InitiatedByUserId,
                    x.DecisionByUserId,
                    x.DecisionAt,
                    x.DecisionRemarks,
                    x.CreatedAt,
                    x.UpdatedAt,
                    x.BranchCode.Value,
                    x.AssignedOfficerUserId,
                    string.Empty,
                    new ApplicantAddress(null, null, null, null, null, null),   // decrypted after the query
                    x.IdentityProofDocumentId,
                    x.TaxProofDocumentId,
                    new StaffLanIds(
                        db.StaffMembers.Where(s => s.UserId == x.InitiatedByUserId).Select(s => s.LanId).FirstOrDefault(),
                        db.StaffMembers.Where(s => s.UserId == x.AssignedOfficerUserId).Select(s => s.LanId).FirstOrDefault(),
                        db.StaffMembers.Where(s => s.UserId == x.IdentityVerification.DecidedByUserId).Select(s => s.LanId).FirstOrDefault(),
                        db.StaffMembers.Where(s => s.UserId == x.DocumentVerification.DecidedByUserId).Select(s => s.LanId).FirstOrDefault(),
                        db.StaffMembers.Where(s => s.UserId == x.DecisionByUserId).Select(s => s.LanId).FirstOrDefault()),
                    x.PersonalDataErasedAt != null)
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
            return null;

        var shown = await personalData.ShowAsync(
            [(row.Detail.CustomerNumber, row.Applicant, row.Detail.PersonalDataErased)],
            cancellationToken);

        return row.Detail with { CustomerName = shown[0].Name, ResidentialAddress = shown[0].Address };
    }

    // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
    // │ BranchCode is stored through a value converter, so the comparison is made
    // │ against a BranchCode value (translated to the stored string), never .Value.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private IQueryable<KycCase> InBranch(BranchCode branch) =>
        db.KycCases.AsNoTracking().Where(x => x.BranchCode == branch);
}