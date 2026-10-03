using EnterpriseWebPlatform.CustomerKyc.Api.Application.Abstractions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Application.Commands.OpenKycCase;

/// <summary>
/// Opens the KYC case for a submitted onboarding application (one case per application,
/// identified by Customer Onboarding's never-repeating ApplicationRef).
/// </summary>
public sealed record OpenKycCaseCommand(
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string? InitiatedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId);

public sealed record OpenKycCaseResult(
    long KycCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string Status,
    bool Created)
{
    public static OpenKycCaseResult From(KycCase kycCase, bool created) => new(
        kycCase.Id,
        kycCase.ApplicationRef,
        kycCase.ApplicationNumber,
        kycCase.CustomerNumber,
        kycCase.BranchCode.Value,
        kycCase.Status.ToCode(),
        created);
}

public sealed class OpenKycCaseCommandHandler(
    IKycCaseRepository repository,
    IKycUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<OpenKycCaseCommandHandler> logger)
{
    /// <summary>
    /// Idempotent: a redelivered trigger returns the existing case. Two concurrent
    /// deliveries race on uq_kyc_cases_application_ref; the loser returns the winner's case.
    /// </summary>
    public async Task<OpenKycCaseResult> HandleAsync(OpenKycCaseCommand command, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByApplicationRefAsync(command.ApplicationRef, cancellationToken);
        if (existing is not null)
            return OpenKycCaseResult.From(existing, created: false);

        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

            var kycCase = KycCase.Open(
                command.ApplicationRef,
                command.ApplicationNumber,
                command.CustomerNumber,
                BranchCode.Create(command.BranchCode),
                command.InitiatedByUserId,
                clock.GetUtcNow());

            repository.Add(kycCase);

            await unitOfWork.SaveChangesAsync(
                new WorkflowContext(command.WorkflowId, command.CorrelationId, command.CausationId),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Opened KYC case {KycCaseId} for application {ApplicationNumber} (ApplicationRef={ApplicationRef}, Branch={Branch}, CustomerNumber={CustomerNumber}). WorkflowId={WorkflowId}, CorrelationId={CorrelationId}, CausationId={CausationId}",
                kycCase.Id, kycCase.ApplicationNumber, kycCase.ApplicationRef, kycCase.BranchCode, kycCase.CustomerNumber,
                command.WorkflowId, command.CorrelationId, command.CausationId);

            return OpenKycCaseResult.From(kycCase, created: true);
        }
        catch (UniqueConstraintViolationException)
        {
            var winner = await repository.GetByApplicationRefAsync(command.ApplicationRef, cancellationToken);
            if (winner is not null)
                return OpenKycCaseResult.From(winner, created: false);

            throw;
        }
    }
}
