using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.DecideVerificationStage;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.AssignKycCase;

public enum AssignmentAction
{
    Claim,
    Release
}

/// <summary>An officer claims a case from the shared queue, or releases their own case back to it.</summary>
public sealed record AssignKycCaseCommand(
    long CaseId,
    AssignmentAction Action,
    string OfficerUserId,
    string? OfficerBranch);

public sealed record AssignKycCaseResult(
    DecideVerificationStageOutcome Outcome,
    string? Error = null,
    string? AssignedOfficerUserId = null);

public sealed class AssignKycCaseCommandHandler(
    IKycCaseRepository repository,
    IKycUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<AssignKycCaseCommandHandler> logger)
{
    public async Task<AssignKycCaseResult> HandleAsync(AssignKycCaseCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var kycCase = await repository.GetForDecisionAsync(command.CaseId, cancellationToken);

        BranchCode.TryCreate(command.OfficerBranch, out var officerBranch);
        if (kycCase is null || !kycCase.IsInBranch(officerBranch))
            return new AssignKycCaseResult(DecideVerificationStageOutcome.NotFound, "KYC case was not found.");

        try
        {
            if (command.Action == AssignmentAction.Claim)
                kycCase.Claim(command.OfficerUserId, clock.GetUtcNow());
            else
                kycCase.Release(command.OfficerUserId, clock.GetUtcNow());
        }
        catch (SeparationOfDutiesViolationException ex)
        {
            return new AssignKycCaseResult(DecideVerificationStageOutcome.Forbidden, ex.Message);
        }
        catch (AssignmentViolationException ex)
        {
            return new AssignKycCaseResult(DecideVerificationStageOutcome.Forbidden, ex.Message);
        }
        catch (DomainConflictException ex)
        {
            return new AssignKycCaseResult(DecideVerificationStageOutcome.Conflict, ex.Message);
        }
        catch (DomainRuleViolationException ex)
        {
            return new AssignKycCaseResult(DecideVerificationStageOutcome.ValidationFailed, ex.Message);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(
                new WorkflowContext(WorkflowId: null, CorrelationId: null, CausationId: Guid.NewGuid()),
                cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return new AssignKycCaseResult(DecideVerificationStageOutcome.Conflict, "The KYC case was changed by another officer. Reload and try again.");
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "KYC case {KycCaseId} {Action} by {OfficerUserId}; assigned officer is now {AssignedOfficerUserId}.",
            kycCase.Id, command.Action, command.OfficerUserId, kycCase.AssignedOfficerUserId ?? "(none)");

        return new AssignKycCaseResult(DecideVerificationStageOutcome.Succeeded, AssignedOfficerUserId: kycCase.AssignedOfficerUserId);
    }
}