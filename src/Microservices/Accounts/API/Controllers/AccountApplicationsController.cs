using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Application.Queries;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Controllers;

public sealed record OfficerDecisionRequest(string? Text, string? Product);

public sealed record OfficerActionResponse(long AccountApplicationId, string Status, string? AssignedOfficerUserId);

/// <summary>
/// Account officer endpoints. Authorization layers:
///  - RBAC / ABAC policies: scope, role, department, permission, minimum clearance (Program.cs);
///  - ABAC branch scope: another branch's application is a 404; no branch claim, no access;
///  - inside the AccountApplication aggregate: ReBAC (assigned officer) and cross-context
///    Separation of Duties (not the initiator, not the Compliance approver).
/// </summary>
[ApiController]
[Route("v1/accounts/applications")]
[Authorize(Policy = "AccountApplicationView")]
public sealed class AccountApplicationsController(
    IAccountsQueries queries,
    OfficerActionCommandHandler actions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<AccountApplicationDetail>>> GetApplications(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (OfficerBranch() is not { } branch)
            return Forbid();

        pageNumber = Math.Clamp(pageNumber, 1, 1000);
        pageSize = Math.Clamp(pageSize, 1, 100);

        AccountApplicationStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!AccountsCodes.TryParseApplicationStatus(status, out var parsed))
                return Ok(new PagedResponse<AccountApplicationDetail>([], pageNumber, pageSize, 0));
            filter = parsed;
        }

        return Ok(await queries.GetApplicationsAsync(branch, pageNumber, pageSize, filter, cancellationToken));
    }

    [HttpGet("{applicationId:long}")]
    public async Task<ActionResult<AccountApplicationDetail>> GetApplication(long applicationId, CancellationToken cancellationToken = default)
    {
        if (OfficerBranch() is not { } branch)
            return Forbid();

        var item = await queries.GetApplicationAsync(applicationId, branch, cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost("{applicationId:long}/claim")]
    [Authorize(Policy = "AccountApplicationReview")]
    public Task<ActionResult<OfficerActionResponse>> Claim(long applicationId, CancellationToken ct = default) =>
        Act(applicationId, OfficerAction.Claim, null, ct);

    [HttpPost("{applicationId:long}/release")]
    [Authorize(Policy = "AccountApplicationReview")]
    public Task<ActionResult<OfficerActionResponse>> Release(long applicationId, CancellationToken ct = default) =>
        Act(applicationId, OfficerAction.Release, null, ct);

    [HttpPost("{applicationId:long}/approve")]
    [Authorize(Policy = "AccountApplicationApprove")]
    public Task<ActionResult<OfficerActionResponse>> Approve(long applicationId, [FromBody] OfficerDecisionRequest request, CancellationToken ct = default) =>
        Act(applicationId, OfficerAction.Approve, request, ct);

    [HttpPost("{applicationId:long}/reject")]
    [Authorize(Policy = "AccountApplicationReject")]
    public Task<ActionResult<OfficerActionResponse>> Reject(long applicationId, [FromBody] OfficerDecisionRequest request, CancellationToken ct = default) =>
        Act(applicationId, OfficerAction.Reject, request, ct);

    [HttpPost("{applicationId:long}/hold")]
    [Authorize(Policy = "AccountApplicationHold")]
    public Task<ActionResult<OfficerActionResponse>> Hold(long applicationId, [FromBody] OfficerDecisionRequest request, CancellationToken ct = default) =>
        Act(applicationId, OfficerAction.Hold, request, ct);

    [HttpPost("{applicationId:long}/release-hold")]
    [Authorize(Policy = "AccountApplicationHold")]
    public Task<ActionResult<OfficerActionResponse>> ReleaseHold(long applicationId, CancellationToken ct = default) =>
        Act(applicationId, OfficerAction.ReleaseHold, null, ct);

    private async Task<ActionResult<OfficerActionResponse>> Act(long applicationId, OfficerAction action, OfficerDecisionRequest? request, CancellationToken ct)
    {
        var officer = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(officer))
            return Unauthorized();
        if (OfficerBranch() is null)
            return Forbid();

        // Each human action is a distinct business command; its id becomes the
        // CausationId of any event it publishes (including the later AccountOpened).
        var result = await actions.HandleAsync(
            new OfficerActionCommand(
                applicationId, action, officer, User.FindFirst("branch")?.Value,
                request?.Text, request?.Product, Guid.NewGuid()),
            ct);

        return result.Outcome switch
        {
            OfficerActionOutcome.NotFound => NotFound(new { error = result.Error }),
            OfficerActionOutcome.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            OfficerActionOutcome.ValidationFailed => BadRequest(new { error = result.Error }),
            OfficerActionOutcome.Conflict => Conflict(new { error = result.Error }),
            _ => Ok(new OfficerActionResponse(result.AccountApplicationId!.Value, result.Status!, result.AssignedOfficerUserId))
        };
    }

    /// <summary>The officer's branch (ABAC); null when the token has none (fail closed).</summary>
    private BranchCode? OfficerBranch() =>
        BranchCode.TryCreate(User.FindFirst("branch")?.Value, out var branch) ? branch : null;
}