using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.AssignKycCase;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.DecideVerificationStage;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Queries;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Controllers;

/// <summary>
/// KYC officer endpoints. Authorization layers:
///  - RBAC / ABAC policies (scope, role, permission, department, clearance; per-stage
///    permission for decisions) - see Program.cs;
///  - ABAC branch scope: an officer sees and acts on their own branch's cases only
///    (another branch's case is a 404); no branch claim = no access (fail closed);
///  - ReBAC + SoD inside the KycCase aggregate (assigned officer only; never the initiator).
/// </summary>
[ApiController]
[Route("v1/kyc/cases")]
[Authorize(Policy = "KycCaseView")]
public sealed class KycCasesController(
    IKycCaseQueries queries,
    DecideVerificationStageCommandHandler decideStageHandler,
    AssignKycCaseCommandHandler assignHandler) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedKycCasesResponse>> GetCases(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? status = null,
        [FromQuery] KycVerificationStage? stage = null,
        CancellationToken cancellationToken = default)
    {
        if (OfficerBranch() is not { } branch)
            return Forbid();

        pageNumber = Math.Clamp(pageNumber, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);

        KycCaseStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            // An unknown status matches no case.
            if (!KycCodes.TryParseCaseStatus(status, out var parsed))
                return Ok(new PagedKycCasesResponse([], pageNumber, pageSize, 0));

            statusFilter = parsed;
        }

        return Ok(await queries.GetCasesAsync(
            branch, pageNumber, pageSize, statusFilter, stage?.ToDomain(), cancellationToken));
    }

    [HttpGet("{caseId:long}")]
    public async Task<ActionResult<KycCaseDetail>> GetCase(
        long caseId,
        CancellationToken cancellationToken = default)
    {
        if (OfficerBranch() is not { } branch)
            return Forbid();

        var item = await queries.GetCaseAsync(caseId, branch, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    /// <summary>ReBAC: take the case from the shared work queue.</summary>
    [HttpPost("{caseId:long}/claim")]
    [Authorize(Policy = "KycCaseAssign")]
    public Task<ActionResult<KycCaseAssignmentResponse>> Claim(long caseId, CancellationToken cancellationToken = default) =>
        Assign(caseId, AssignmentAction.Claim, cancellationToken);

    /// <summary>ReBAC: return your own case to the shared work queue.</summary>
    [HttpPost("{caseId:long}/release")]
    [Authorize(Policy = "KycCaseAssign")]
    public Task<ActionResult<KycCaseAssignmentResponse>> Release(long caseId, CancellationToken cancellationToken = default) =>
        Assign(caseId, AssignmentAction.Release, cancellationToken);

    [HttpPost("{caseId:long}/identity-verification/approve")]
    [Authorize(Policy = "KycIdentityApprove")]
    public Task<ActionResult<KycCaseDecisionResponse>> ApproveIdentity(
        long caseId,
        [FromBody] KycCaseDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        DecideStage(caseId, VerificationStageType.IdentityVerification, StageDecision.Approve, request, cancellationToken);

    [HttpPost("{caseId:long}/identity-verification/reject")]
    [Authorize(Policy = "KycIdentityReject")]
    public Task<ActionResult<KycCaseDecisionResponse>> RejectIdentity(
        long caseId,
        [FromBody] KycCaseDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        DecideStage(caseId, VerificationStageType.IdentityVerification, StageDecision.Reject, request, cancellationToken);

    [HttpPost("{caseId:long}/document-verification/approve")]
    [Authorize(Policy = "KycDocumentApprove")]
    public Task<ActionResult<KycCaseDecisionResponse>> ApproveDocument(
        long caseId,
        [FromBody] KycCaseDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        DecideStage(caseId, VerificationStageType.DocumentVerification, StageDecision.Approve, request, cancellationToken);

    [HttpPost("{caseId:long}/document-verification/reject")]
    [Authorize(Policy = "KycDocumentReject")]
    public Task<ActionResult<KycCaseDecisionResponse>> RejectDocument(
        long caseId,
        [FromBody] KycCaseDecisionRequest request,
        CancellationToken cancellationToken = default) =>
        DecideStage(caseId, VerificationStageType.DocumentVerification, StageDecision.Reject, request, cancellationToken);

    private async Task<ActionResult<KycCaseDecisionResponse>> DecideStage(
        long caseId,
        VerificationStageType stage,
        StageDecision decision,
        KycCaseDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var decisionByUserId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(decisionByUserId))
            return Unauthorized();

        if (OfficerBranch() is null)
            return Forbid();

        // Each human approval/rejection is a distinct business command; its id
        // becomes the CausationId of the resulting stage event.
        var result = await decideStageHandler.HandleAsync(
            new DecideVerificationStageCommand(
                caseId, stage, decision, decisionByUserId, User.FindFirst("branch")?.Value,
                request.DecisionRemarks, Guid.NewGuid()),
            cancellationToken);

        return result.Outcome switch
        {
            DecideVerificationStageOutcome.NotFound => NotFound(new { error = result.Error }),
            DecideVerificationStageOutcome.Forbidden => Forbid(),
            DecideVerificationStageOutcome.ValidationFailed => BadRequest(new { error = result.Error }),
            DecideVerificationStageOutcome.Conflict => Conflict(new { error = result.Error }),
            _ => Ok(new KycCaseDecisionResponse(
                result.KycCaseId!.Value,
                result.CustomerNumber!,
                KycVerificationStageMapping.FromDomain(result.Stage!.Value),
                result.StageStatus!,
                result.OverallStatus!,
                result.DecisionByUserId!,
                result.DecisionAt!.Value,
                result.DecisionRemarks))
        };
    }

    private async Task<ActionResult<KycCaseAssignmentResponse>> Assign(
        long caseId,
        AssignmentAction action,
        CancellationToken cancellationToken)
    {
        var officerUserId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(officerUserId))
            return Unauthorized();

        if (OfficerBranch() is null)
            return Forbid();

        var result = await assignHandler.HandleAsync(
            new AssignKycCaseCommand(caseId, action, officerUserId, User.FindFirst("branch")?.Value),
            cancellationToken);

        return result.Outcome switch
        {
            DecideVerificationStageOutcome.NotFound => NotFound(new { error = result.Error }),
            DecideVerificationStageOutcome.Forbidden => Forbid(),
            DecideVerificationStageOutcome.ValidationFailed => BadRequest(new { error = result.Error }),
            DecideVerificationStageOutcome.Conflict => Conflict(new { error = result.Error }),
            _ => Ok(new KycCaseAssignmentResponse(caseId, result.AssignedOfficerUserId))
        };
    }

    /// <summary>The officer's branch (ABAC); null when the token has none (fail closed).</summary>
    private BranchCode? OfficerBranch() =>
        BranchCode.TryCreate(User.FindFirst("branch")?.Value, out var branch) ? branch : null;
}