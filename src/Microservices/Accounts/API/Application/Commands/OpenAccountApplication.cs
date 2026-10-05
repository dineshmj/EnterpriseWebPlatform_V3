using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Application.Commands;

/// <summary>
/// Opens the account application for an onboarding application that Compliance approved.
/// CausationId is the triggering ComplianceCaseApproved MessageId (Inbox key and cause of
/// the new event).
/// </summary>
public sealed record OpenAccountApplicationCommand(
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    long ComplianceCaseId,
    string BranchCode,
    string? InitiatedByUserId,
    string? ComplianceApprovedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId);

public sealed record OpenAccountApplicationResult(long AccountApplicationId, string Status, bool Created);

public sealed class OpenAccountApplicationCommandHandler(
    IAccountApplicationRepository repository,
    IInboxStore inbox,
    IAccountsUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<OpenAccountApplicationCommandHandler> logger)
{
    public const string InboxConsumer = "accounts.application-opening";

    /// <summary>
    /// Idempotent twice over: per message (Inbox) and per application (one account
    /// application per ApplicationRef). Concurrent deliveries race on the unique keys;
    /// the loser returns the winner's application.
    /// </summary>
    public async Task<OpenAccountApplicationResult> HandleAsync(OpenAccountApplicationCommand command, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByApplicationRefAsync(command.ApplicationRef, cancellationToken);
        if (existing is not null)
            return new OpenAccountApplicationResult(existing.Id, existing.Status.ToCode(), Created: false);

        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

            var application = AccountApplication.Open(
                command.ApplicationRef,
                command.ApplicationNumber,
                command.CustomerNumber,
                command.ComplianceCaseId,
                BranchCode.Create(command.BranchCode),
                command.InitiatedByUserId,
                command.ComplianceApprovedByUserId,
                clock.GetUtcNow());

            repository.Add(application);
            inbox.RecordProcessed(command.CausationId, InboxConsumer);

            await unitOfWork.SaveChangesAsync(
                new WorkflowContext(command.WorkflowId, command.CorrelationId, command.CausationId),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Opened account application {AccountApplicationId} for application {ApplicationNumber} (compliance case {ComplianceCaseId}, branch {Branch}); awaiting an account officer.",
                application.Id, application.ApplicationNumber, application.ComplianceCaseId, application.BranchCode);

            return new OpenAccountApplicationResult(application.Id, application.Status.ToCode(), Created: true);
        }
        catch (UniqueConstraintViolationException)
        {
            var winner = await repository.GetByApplicationRefAsync(command.ApplicationRef, cancellationToken);
            if (winner is not null)
                return new OpenAccountApplicationResult(winner.Id, winner.Status.ToCode(), Created: false);
            throw;
        }
    }
}