using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Application.Commands;

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
/// An account officer acts on an application. OfficerBranch comes from the officer's
/// token (ABAC); CommandId becomes the CausationId of any published event. Product is
/// used by Approve only (default: everyday transaction account).
/// </summary>
public sealed record OfficerActionCommand(
    long ApplicationId,
    OfficerAction Action,
    string OfficerUserId,
    string? OfficerBranch,
    string? Text,
    string? Product,
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
    long? AccountApplicationId = null,
    string? Status = null,
    string? AssignedOfficerUserId = null)
{
    public static OfficerActionResult Failed(OfficerActionOutcome outcome, string error) => new(outcome, error);
}

/// <summary>
/// One handler for every officer action: lock the application, apply the aggregate
/// method, save with the Outbox, commit. Domain refusals map to 403 (SoD, ReBAC),
/// 409 (state) or 400 (input).
/// </summary>
public sealed class OfficerActionCommandHandler(
    IAccountApplicationRepository repository,
    IAccountsUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<OfficerActionCommandHandler> logger)
{
    public async Task<OfficerActionResult> HandleAsync(OfficerActionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var application = await repository.GetForUpdateAsync(command.ApplicationId, cancellationToken);

        // ABAC: another branch's application is reported as not found (existence is not disclosed).
        BranchCode.TryCreate(command.OfficerBranch, out var officerBranch);
        if (application is null || !application.IsInBranch(officerBranch))
            return OfficerActionResult.Failed(OfficerActionOutcome.NotFound, "Account application was not found.");

        var now = clock.GetUtcNow();

        try
        {
            var text = OfficerText.From(command.Text);
            switch (command.Action)
            {
                case OfficerAction.Claim: application.Claim(command.OfficerUserId, now); break;
                case OfficerAction.Release: application.Release(command.OfficerUserId, now); break;
                case OfficerAction.Approve: application.Approve(command.OfficerUserId, ProductOf(command.Product), text, command.CommandId, now); break;
                case OfficerAction.Reject: application.Reject(command.OfficerUserId, text, command.CommandId, now); break;
                case OfficerAction.Hold: application.PlaceOnHold(command.OfficerUserId, text, now); break;
                case OfficerAction.ReleaseHold: application.ReleaseHold(command.OfficerUserId, now); break;
                default: throw new ArgumentOutOfRangeException(nameof(command), command.Action, null);
            }
        }
        catch (Exception ex) when (ex is SeparationOfDutiesViolationException or AssignmentViolationException)
        {
            logger.LogWarning(
                "{Rule} denied {Action} on account application {AccountApplicationId} by {UserId}: {Reason}",
                ex is SeparationOfDutiesViolationException ? "SoD" : "ReBAC",
                command.Action, command.ApplicationId, command.OfficerUserId, ex.Message);
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
            return OfficerActionResult.Failed(OfficerActionOutcome.Conflict, "The account application was changed by someone else. Reload and try again.");
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Account application {AccountApplicationId}: {Action} by {UserId}; status {Status}. CausationId={CausationId}",
            application.Id, command.Action, command.OfficerUserId, application.Status.ToCode(), command.CommandId);

        return new OfficerActionResult(
            OfficerActionOutcome.Succeeded,
            AccountApplicationId: application.Id,
            Status: application.Status.ToCode(),
            AssignedOfficerUserId: application.AssignedOfficerUserId);
    }

    private static AccountProduct ProductOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return AccountProduct.EverydayTransaction;

        return AccountsCodes.TryParseProduct(code, out var product)
            ? product
            : throw new DomainRuleViolationException("Product must be EVERYDAY_TRANSACTION or SAVINGS.");
    }
}