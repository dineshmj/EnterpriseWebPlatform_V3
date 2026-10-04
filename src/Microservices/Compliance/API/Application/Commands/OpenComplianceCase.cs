using EnterpriseWebPlatform.Compliance.Api.Application.Abstractions;
using EnterpriseWebPlatform.Compliance.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Compliance.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Compliance.Api.Application.Commands;

/// <summary>
/// Opens the compliance case for an application that KYC approved. CausationId is
/// the triggering KycCaseApproved MessageId (Inbox key and cause of the new event).
/// </summary>
public sealed record OpenComplianceCaseCommand(
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    long KycCaseId,
    string BranchCode,
    string? InitiatedByUserId,
    string? KycIdentityDecidedByUserId,
    string? KycDocumentDecidedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId);

public sealed record OpenComplianceCaseResult(long ComplianceCaseId, string Status, bool Created);

public sealed class OpenComplianceCaseCommandHandler(
    IComplianceCaseRepository repository,
    IInboxStore inbox,
    IComplianceUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<OpenComplianceCaseCommandHandler> logger)
{
    public const string InboxConsumer = "compliance.case-opening";

    /// <summary>
    /// Idempotent twice over: per message (Inbox) and per application (one case per
    /// ApplicationRef). Concurrent deliveries race on the unique keys; the loser
    /// returns the winner's case.
    /// </summary>
    public async Task<OpenComplianceCaseResult> HandleAsync(OpenComplianceCaseCommand command, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByApplicationRefAsync(command.ApplicationRef, cancellationToken);
        if (existing is not null)
            return new OpenComplianceCaseResult(existing.Id, existing.Status.ToCode(), Created: false);

        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

            var complianceCase = ComplianceCase.Open(
                command.ApplicationRef,
                command.ApplicationNumber,
                command.CustomerNumber,
                command.KycCaseId,
                BranchCode.Create(command.BranchCode),
                command.InitiatedByUserId,
                command.KycIdentityDecidedByUserId,
                command.KycDocumentDecidedByUserId,
                clock.GetUtcNow());

            repository.Add(complianceCase);
            inbox.RecordProcessed(command.CausationId, InboxConsumer);

            await unitOfWork.SaveChangesAsync(
                new WorkflowContext(command.WorkflowId, command.CorrelationId, command.CausationId),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Opened compliance case {ComplianceCaseId} for application {ApplicationNumber} (KYC case {KycCaseId}, branch {Branch}); screening is due.",
                complianceCase.Id, complianceCase.ApplicationNumber, complianceCase.KycCaseId, complianceCase.BranchCode);

            return new OpenComplianceCaseResult(complianceCase.Id, complianceCase.Status.ToCode(), Created: true);
        }
        catch (UniqueConstraintViolationException)
        {
            var winner = await repository.GetByApplicationRefAsync(command.ApplicationRef, cancellationToken);
            if (winner is not null)
                return new OpenComplianceCaseResult(winner.Id, winner.Status.ToCode(), Created: false);
            throw;
        }
    }
}