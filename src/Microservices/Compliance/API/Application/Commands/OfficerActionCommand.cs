using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Application.Commands;

public enum OfficerAction
{
    Claim,
    Release,
    Approve,
    Reject,
    Hold,
    ReleaseHold
}

/// <summary>
/// A compliance officer acts on a case. OfficerBranch and OfficerClearance come from
/// the officer's token (ABAC); CommandId becomes the CausationId of any published event.
/// </summary>
public sealed record OfficerActionCommand(
    long CaseId,
    OfficerAction Action,
    string OfficerUserId,
    string? OfficerBranch,
    int OfficerClearance,
    string? Text,
    Guid CommandId);

public enum OfficerActionOutcome
{
    Succeeded,
    NotFound,
    ValidationFailed,
    Forbidden,
    Conflict
}

public sealed record OfficerActionResult(
    OfficerActionOutcome Outcome,
    string? Error = null,
    long? ComplianceCaseId = null,
    string? Status = null,
    string? AssignedOfficerUserId = null)
{
    public static OfficerActionResult Failed(OfficerActionOutcome outcome, string error) => new(outcome, error);
}

/// <summary>
/// One handler for every officer action: lock the case, apply the aggregate method,
/// save with the Outbox, commit. Domain refusals map to 403 (SoD, ReBAC, clearance),
/// 409 (state) or 400 (input).
/// </summary>
public sealed class OfficerActionCommandHandler(
    IComplianceCaseRepository repository,
    IComplianceUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<OfficerActionCommandHandler> logger)
{
    public async Task<OfficerActionResult> HandleAsync(OfficerActionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var complianceCase = await repository.GetForUpdateAsync(command.CaseId, cancellationToken);

        // ABAC: another branch's case is reported as not found (existence is not disclosed).
        BranchCode.TryCreate(command.OfficerBranch, out var officerBranch);
        if (complianceCase is null || !complianceCase.IsInBranch(officerBranch))
            return OfficerActionResult.Failed(OfficerActionOutcome.NotFound, "Compliance case was not found.");

        var now = clock.GetUtcNow();

        try
        {
            var text = OfficerText.From(command.Text);
            switch (command.Action)
            {
                case OfficerAction.Claim: complianceCase.Claim(command.OfficerUserId, now); break;
                case OfficerAction.Release: complianceCase.Release(command.OfficerUserId, now); break;
                case OfficerAction.Approve: complianceCase.Approve(command.OfficerUserId, command.OfficerClearance, text, now); break;
                case OfficerAction.Reject: complianceCase.Reject(command.OfficerUserId, text, now); break;
                case OfficerAction.Hold: complianceCase.PlaceOnHold(command.OfficerUserId, text, now); break;
                case OfficerAction.ReleaseHold: complianceCase.ReleaseHold(command.OfficerUserId, now); break;
                default: throw new ArgumentOutOfRangeException(nameof(command), command.Action, null);
            }
        }
        catch (Exception ex) when (ex is SeparationOfDutiesViolationException or AssignmentViolationException or ClearanceViolationException)
        {
            logger.LogWarning(
                "{Rule} denied {Action} on compliance case {ComplianceCaseId} by {UserId}: {Reason}",
                ex switch { SeparationOfDutiesViolationException => "SoD", AssignmentViolationException => "ReBAC", _ => "Clearance (ABAC)" },
                command.Action, command.CaseId, command.OfficerUserId, ex.Message);
            return OfficerActionResult.Failed(OfficerActionOutcome.Forbidden, ex.Message);
        }
        catch (DomainConflictException ex)
        {
            return OfficerActionResult.Failed(OfficerActionOutcome.Conflict, ex.Message);
        }
        catch (DomainRuleViolationException ex)
        {
            return OfficerActionResult.Failed(OfficerActionOutcome.ValidationFailed, ex.Message);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(
                new WorkflowContext(WorkflowId: null, CorrelationId: null, CausationId: command.CommandId),
                cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return OfficerActionResult.Failed(OfficerActionOutcome.Conflict, "The compliance case was changed by someone else. Reload and try again.");
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Compliance case {ComplianceCaseId}: {Action} by {UserId}; status {Status}. CausationId={CausationId}",
            complianceCase.Id, command.Action, command.OfficerUserId, complianceCase.Status.ToCode(), command.CommandId);

        return new OfficerActionResult(
            OfficerActionOutcome.Succeeded,
            ComplianceCaseId: complianceCase.Id,
            Status: complianceCase.Status.ToCode(),
            AssignedOfficerUserId: complianceCase.AssignedOfficerUserId);
    }
}