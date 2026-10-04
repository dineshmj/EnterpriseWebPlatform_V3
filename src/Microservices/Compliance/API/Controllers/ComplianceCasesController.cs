using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Compliance.Api.Application.Commands;
using EnterpriseWebPlatform.Compliance.Api.Application.Queries;
using EnterpriseWebPlatform.Compliance.Api.Authorization;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Controllers;

public sealed record OfficerTextRequest(string? Text);

public sealed record OfficerActionResponse(long ComplianceCaseId, string Status, string? AssignedOfficerUserId);

/// <summary>
/// Compliance officer endpoints. Authorization layers:
///  - RBAC / ABAC policies: scope, role, department, permission, minimum clearance (Program.cs);
///  - ABAC branch scope: another branch's case is a 404; no branch claim, no access;
///  - inside the ComplianceCase aggregate: ReBAC (assigned officer), cross-context
///    Separation of Duties, and the clearance the case's risk requires to approve.
/// </summary>
[ApiController]
[Route("v1/compliance/cases")]
[Authorize(Policy = "ComplianceCaseView")]
public sealed class ComplianceCasesController(
    IComplianceCaseQueries queries,
    OfficerActionCommandHandler actions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedComplianceCasesResponse>> GetCases(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (OfficerBranch() is not { } branch)
            return Forbid();

        pageNumber = Math.Clamp(pageNumber, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);

        ComplianceCaseStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!ComplianceCodes.TryParseStatus(status, out var parsed))
                return Ok(new PagedComplianceCasesResponse([], pageNumber, pageSize, 0));
            filter = parsed;
        }

        return Ok(await queries.GetCasesAsync(branch, pageNumber, pageSize, filter, cancellationToken));
    }

    [HttpGet("{caseId:long}")]
    public async Task<ActionResult<ComplianceCaseDetail>> GetCase(long caseId, CancellationToken cancellationToken = default)
    {
        if (OfficerBranch() is not { } branch)
            return Forbid();

        var item = await queries.GetCaseAsync(caseId, branch, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost("{caseId:long}/claim")]
    [Authorize(Policy = "ComplianceCaseReview")]
    public Task<ActionResult<OfficerActionResponse>> Claim(long caseId, CancellationToken ct = default) =>
        Act(caseId, OfficerAction.Claim, null, ct);

    [HttpPost("{caseId:long}/release")]
    [Authorize(Policy = "ComplianceCaseReview")]
    public Task<ActionResult<OfficerActionResponse>> Release(long caseId, CancellationToken ct = default) =>
        Act(caseId, OfficerAction.Release, null, ct);

    [HttpPost("{caseId:long}/approve")]
    [Authorize(Policy = "ComplianceCaseApprove")]
    public Task<ActionResult<OfficerActionResponse>> Approve(long caseId, [FromBody] OfficerTextRequest request, CancellationToken ct = default) =>
        Act(caseId, OfficerAction.Approve, request.Text, ct);

    [HttpPost("{caseId:long}/reject")]
    [Authorize(Policy = "ComplianceCaseReject")]
    public Task<ActionResult<OfficerActionResponse>> Reject(long caseId, [FromBody] OfficerTextRequest request, CancellationToken ct = default) =>
        Act(caseId, OfficerAction.Reject, request.Text, ct);

    [HttpPost("{caseId:long}/hold")]
    [Authorize(Policy = "ComplianceCaseHold")]
    public Task<ActionResult<OfficerActionResponse>> Hold(long caseId, [FromBody] OfficerTextRequest request, CancellationToken ct = default) =>
        Act(caseId, OfficerAction.Hold, request.Text, ct);

    [HttpPost("{caseId:long}/release-hold")]
    [Authorize(Policy = "ComplianceCaseReleaseHold")]
    public Task<ActionResult<OfficerActionResponse>> ReleaseHold(long caseId, CancellationToken ct = default) =>
        Act(caseId, OfficerAction.ReleaseHold, null, ct);

    private async Task<ActionResult<OfficerActionResponse>> Act(long caseId, OfficerAction action, string? text, CancellationToken ct)
    {
        var officer = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(officer))
            return Unauthorized();
        if (OfficerBranch() is null)
            return Forbid();

        // Each human action is a distinct business command; its id becomes the
        // CausationId of any event it publishes.
        var result = await actions.HandleAsync(
            new OfficerActionCommand(
                caseId, action, officer, User.FindFirst("branch")?.Value,
                ComplianceOfficerAuthorizationHandler.ClearanceOf(User), text, Guid.NewGuid()),
            ct);

        return result.Outcome switch
        {
            OfficerActionOutcome.NotFound => NotFound(new { error = result.Error }),
            OfficerActionOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            OfficerActionOutcome.ValidationFailed => BadRequest(new { error = result.Error }),
            OfficerActionOutcome.Conflict => Conflict(new { error = result.Error }),
            _ => Ok(new OfficerActionResponse(result.ComplianceCaseId!.Value, result.Status!, result.AssignedOfficerUserId))
        };
    }

    /// <summary>The officer's branch (ABAC); null when the token has none (fail closed).</summary>
    private BranchCode? OfficerBranch() =>
        BranchCode.TryCreate(User.FindFirst("branch")?.Value, out var branch) ? branch : null;
}