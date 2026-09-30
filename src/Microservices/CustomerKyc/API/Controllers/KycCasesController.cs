using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

[ApiController]
[Route("v1/kyc/cases")]
[Authorize(Policy = "KycCaseView")]
public sealed class KycCasesController(KycDbContext db, KycCaseService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedKycCasesResponse>> GetCases(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? status = null,
        [FromQuery] KycVerificationStage? stage = null,
        CancellationToken cancellationToken = default)
    {
        pageNumber = Math.Clamp(pageNumber, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.KycCases.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status);

        if (stage.HasValue)
        {
            query = stage.Value == KycVerificationStage.IdentityVerification
                ? query.Where(x => x.IdentityVerificationStatus == "PENDING_REVIEW")
                : query.Where(x => x.DocumentVerificationStatus == "PENDING_REVIEW");
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(x => x.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new KycCaseListItem(
                x.Id,
                x.CustomerNumber,
                x.Status,
                x.IdentityVerificationStatus,
                x.IdentityVerificationByUserId,
                x.IdentityVerificationAt,
                x.IdentityVerificationRemarks,
                x.DocumentVerificationStatus,
                x.DocumentVerificationByUserId,
                x.DocumentVerificationAt,
                x.DocumentVerificationRemarks,
                x.InitiatedByUserId,
                x.DecisionByUserId,
                x.DecisionAt,
                x.DecisionRemarks,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Ok(new PagedKycCasesResponse(items, pageNumber, pageSize, totalCount));
    }

    [HttpGet("{caseId:long}")]
    public async Task<ActionResult<KycCaseDetail>> GetCase(
        long caseId,
        CancellationToken cancellationToken = default)
    {
        var item = await db.KycCases
            .AsNoTracking()
            .Where(x => x.Id == caseId)
            .Select(x => new KycCaseDetail(
                x.Id,
                x.CustomerNumber,
                x.Status,
                x.IdentityVerificationStatus,
                x.IdentityVerificationByUserId,
                x.IdentityVerificationAt,
                x.IdentityVerificationRemarks,
                x.DocumentVerificationStatus,
                x.DocumentVerificationByUserId,
                x.DocumentVerificationAt,
                x.DocumentVerificationRemarks,
                x.InitiatedByUserId,
                x.DecisionByUserId,
                x.DecisionAt,
                x.DecisionRemarks,
                x.CreatedAt,
                x.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);

        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost("{caseId:long}/identity-verification/approve")]
    [Authorize(Policy = "KycCaseApprove")]
    public Task<ActionResult<KycCaseDecisionResponse>> ApproveIdentity(
        long caseId,
        [FromBody] KycCaseDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        DecideStage(caseId, KycVerificationStage.IdentityVerification,
            KycCaseDecision.Approve, request, cancellationToken);

    [HttpPost("{caseId:long}/identity-verification/reject")]
    [Authorize(Policy = "KycCaseReject")]
    public Task<ActionResult<KycCaseDecisionResponse>> RejectIdentity(
        long caseId,
        [FromBody] KycCaseDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        DecideStage(caseId, KycVerificationStage.IdentityVerification,
            KycCaseDecision.Reject, request, cancellationToken);

    [HttpPost("{caseId:long}/document-verification/approve")]
    [Authorize(Policy = "KycCaseApprove")]
    public Task<ActionResult<KycCaseDecisionResponse>> ApproveDocument(
        long caseId,
        [FromBody] KycCaseDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        DecideStage(caseId, KycVerificationStage.DocumentVerification,
            KycCaseDecision.Approve, request, cancellationToken);

    [HttpPost("{caseId:long}/document-verification/reject")]
    [Authorize(Policy = "KycCaseReject")]
    public Task<ActionResult<KycCaseDecisionResponse>> RejectDocument(
        long caseId,
        [FromBody] KycCaseDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        DecideStage(caseId, KycVerificationStage.DocumentVerification,
            KycCaseDecision.Reject, request, cancellationToken);

    private async Task<ActionResult<KycCaseDecisionResponse>> DecideStage(
        long caseId,
        KycVerificationStage stage,
        KycCaseDecision decision,
        KycCaseDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var decisionByUserId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(decisionByUserId))
            return Unauthorized();

        var result = await service.DecideStageAsync(
            caseId, stage, decision, decisionByUserId,
            request.DecisionRemarks, cancellationToken);

        if (result.IsNotFound)
            return NotFound(new { error = result.Error });

        if (result.IsForbidden)
            return Forbid();

        if (result.IsValidationFailure)
            return BadRequest(new { error = result.Error });

        if (result.IsConflict)
            return Conflict(new { error = result.Error });

        return Ok(new KycCaseDecisionResponse(
            result.KycCaseId!.Value,
            result.CustomerNumber!,
            result.Stage!.Value,
            result.StageStatus!,
            result.OverallStatus!,
            result.DecisionByUserId!,
            result.DecisionAt!.Value,
            result.DecisionRemarks));
    }
}

public sealed record KycCaseListItem(
    long KycCaseId,
    string CustomerNumber,
    string Status,
    string IdentityVerificationStatus,
    string? IdentityVerificationByUserId,
    DateTimeOffset? IdentityVerificationAt,
    string? IdentityVerificationRemarks,
    string DocumentVerificationStatus,
    string? DocumentVerificationByUserId,
    DateTimeOffset? DocumentVerificationAt,
    string? DocumentVerificationRemarks,
    string? InitiatedByUserId,
    string? DecisionByUserId,
    DateTimeOffset? DecisionAt,
    string? DecisionRemarks,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PagedKycCasesResponse(
    IReadOnlyList<KycCaseListItem> Items,
    int PageNumber,
    int PageSize,
    int TotalCount);

public sealed record KycCaseDetail(
    long KycCaseId,
    string CustomerNumber,
    string Status,
    string IdentityVerificationStatus,
    string? IdentityVerificationByUserId,
    DateTimeOffset? IdentityVerificationAt,
    string? IdentityVerificationRemarks,
    string DocumentVerificationStatus,
    string? DocumentVerificationByUserId,
    DateTimeOffset? DocumentVerificationAt,
    string? DocumentVerificationRemarks,
    string? InitiatedByUserId,
    string? DecisionByUserId,
    DateTimeOffset? DecisionAt,
    string? DecisionRemarks,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);