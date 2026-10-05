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
    Guid CausationId,
    Applicant Applicant,
    Guid IdentityProofDocumentId,
    Guid TaxProofDocumentId);

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
    IInboxStore inbox,
    IKycUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<OpenKycCaseCommandHandler> logger)
{
    /// <summary>Inbox consumer name for onboarding.application.submitted.</summary>
    public const string InboxConsumer = "customer-kyc.case-opening";

    /// <summary>
    /// Idempotent twice over:
    ///  - per message (Inbox): the triggering message (CausationId) is recorded in the
    ///    same transaction as the new case, so a redelivery is recognised as such;
    ///  - per application (business key): one case per ApplicationRef, so even a
    ///    re-published event with a new MessageId returns the existing case.
    /// Two concurrent deliveries race on the unique keys; the loser returns the winner's case.
    /// </summary>
    public async Task<OpenKycCaseResult> HandleAsync(OpenKycCaseCommand command, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByApplicationRefAsync(command.ApplicationRef, cancellationToken);
        if (existing is not null)
        {
            if (!await inbox.HasProcessedAsync(command.CausationId, InboxConsumer, cancellationToken))
            {
                logger.LogInformation(
                    "Message {MessageId} asks again for the KYC case of application {ApplicationRef}; case {KycCaseId} already exists.",
                    command.CausationId, command.ApplicationRef, existing.Id);
            }

            return OpenKycCaseResult.From(existing, created: false);
        }

        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

            var kycCase = KycCase.Open(
                command.ApplicationRef,
                command.ApplicationNumber,
                command.CustomerNumber,
                BranchCode.Create(command.BranchCode),
                command.Applicant,
                command.IdentityProofDocumentId,
                command.TaxProofDocumentId,
                command.InitiatedByUserId,
                clock.GetUtcNow());

            repository.Add(kycCase);

            // The Inbox row, the new case and its Outbox event commit together.
            inbox.RecordProcessed(command.CausationId, InboxConsumer);

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