using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Queries;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

public sealed class KycCaseQueries(KycDbContext db) : IKycCaseQueries
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

        var items = await query
            .OrderBy(x => x.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new KycCaseListItem(
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
                x.Applicant.FirstName + " " + x.Applicant.LastName,
                new ApplicantAddress(
                    x.Applicant.AddressLine1,
                    x.Applicant.AddressLine2,
                    x.Applicant.City,
                    x.Applicant.State,
                    x.Applicant.PostalCode,
                    x.Applicant.CountryCode)))
            .ToListAsync(cancellationToken);

        return new PagedKycCasesResponse(items, pageNumber, pageSize, totalCount);
    }

    public Task<KycCaseDetail?> GetCaseAsync(long caseId, BranchCode branch, CancellationToken cancellationToken) =>
        InBranch(branch)
            .Where(x => x.Id == caseId)
            .Select(x => new KycCaseDetail(
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
                x.Applicant.FirstName + " " + x.Applicant.LastName,
                new ApplicantAddress(
                    x.Applicant.AddressLine1,
                    x.Applicant.AddressLine2,
                    x.Applicant.City,
                    x.Applicant.State,
                    x.Applicant.PostalCode,
                    x.Applicant.CountryCode),
                x.IdentityProofDocumentId,
                x.TaxProofDocumentId))
            .SingleOrDefaultAsync(cancellationToken);

    // BranchCode is stored through a value converter, so the comparison is made
    // against a BranchCode value (translated to the stored string), never .Value.
    private IQueryable<KycCase> InBranch(BranchCode branch) =>
        db.KycCases.AsNoTracking().Where(x => x.BranchCode == branch);
}