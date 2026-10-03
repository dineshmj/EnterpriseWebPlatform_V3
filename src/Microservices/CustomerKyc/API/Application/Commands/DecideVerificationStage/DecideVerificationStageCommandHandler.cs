using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.DecideVerificationStage;

/// <summary>
/// An officer approves or rejects one verification stage. CommandId identifies this
/// human command and becomes the CausationId of the resulting stage event.
/// OfficerBranch is the officer's branch claim (ABAC: own-branch cases only).
/// </summary>
public sealed record DecideVerificationStageCommand(
    long CaseId,
    VerificationStageType Stage,
    StageDecision Decision,
    string DecidedByUserId,
    string? OfficerBranch,
    string? Remarks,
    Guid CommandId);

public enum DecideVerificationStageOutcome
{
    Succeeded,
    NotFound,
    ValidationFailed,
    Forbidden,
    Conflict
}

public sealed record DecideVerificationStageResult(
    DecideVerificationStageOutcome Outcome,
    string? Error = null,
    long? KycCaseId = null,
    string? CustomerNumber = null,
    VerificationStageType? Stage = null,
    string? StageStatus = null,
    string? OverallStatus = null,
    string? DecisionByUserId = null,
    DateTimeOffset? DecisionAt = null,
    string? DecisionRemarks = null)
{
    public static DecideVerificationStageResult Failed(DecideVerificationStageOutcome outcome, string error) =>
        new(outcome, error);
}

public sealed class DecideVerificationStageCommandHandler(
    IKycCaseRepository repository,
    IKycUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<DecideVerificationStageCommandHandler> logger)
{
    public async Task<DecideVerificationStageResult> HandleAsync(
        DecideVerificationStageCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var kycCase = await repository.GetForDecisionAsync(command.CaseId, cancellationToken);

        // ABAC: a case of another branch is reported as not found (existence is not disclosed).
        BranchCode.TryCreate(command.OfficerBranch, out var officerBranch);
        if (kycCase is null || !kycCase.IsInBranch(officerBranch))
            return DecideVerificationStageResult.Failed(DecideVerificationStageOutcome.NotFound, "KYC case was not found.");

        var now = clock.GetUtcNow();

        try
        {
            kycCase.DecideStage(
                command.Stage,
                command.Decision,
                command.DecidedByUserId,
                DecisionRemarks.From(command.Remarks),
                now);
        }
        catch (SeparationOfDutiesViolationException ex)
        {
            logger.LogWarning(
                "Separation of Duties denied {Decision} of {Stage} on KYC case {KycCaseId} by {UserId}: {Reason}",
                command.Decision, command.Stage, command.CaseId, command.DecidedByUserId, ex.Message);
            return DecideVerificationStageResult.Failed(DecideVerificationStageOutcome.Forbidden, ex.Message);
        }
        catch (AssignmentViolationException ex)
        {
            logger.LogWarning(
                "ReBAC denied {Decision} of {Stage} on KYC case {KycCaseId} by {UserId}: {Reason}",
                command.Decision, command.Stage, command.CaseId, command.DecidedByUserId, ex.Message);
            return DecideVerificationStageResult.Failed(DecideVerificationStageOutcome.Forbidden, ex.Message);
        }
        catch (DomainConflictException ex)
        {
            return DecideVerificationStageResult.Failed(DecideVerificationStageOutcome.Conflict, ex.Message);
        }
        catch (DomainRuleViolationException ex)
        {
            return DecideVerificationStageResult.Failed(DecideVerificationStageOutcome.ValidationFailed, ex.Message);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(
                new WorkflowContext(WorkflowId: null, CorrelationId: null, CausationId: command.CommandId),
                cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return DecideVerificationStageResult.Failed(
                DecideVerificationStageOutcome.Conflict,
                "The KYC case was changed by another decision. Reload and try again.");
        }

        await transaction.CommitAsync(cancellationToken);

        var stage = kycCase.StageOf(command.Stage);

        logger.LogInformation(
            "KYC case {KycCaseId} {Stage} changed from PENDING_REVIEW to {StageStatus} by {DecisionByUserId}. OverallStatus={OverallStatus}, CausationId={CausationId}",
            kycCase.Id, command.Stage, stage.Status.ToCode(), command.DecidedByUserId,
            kycCase.Status.ToCode(), command.CommandId);

        return new DecideVerificationStageResult(
            DecideVerificationStageOutcome.Succeeded,
            KycCaseId: kycCase.Id,
            CustomerNumber: kycCase.CustomerNumber,
            Stage: command.Stage,
            StageStatus: stage.Status.ToCode(),
            OverallStatus: kycCase.Status.ToCode(),
            DecisionByUserId: command.DecidedByUserId,
            DecisionAt: now,
            DecisionRemarks: stage.Remarks);
    }
}
