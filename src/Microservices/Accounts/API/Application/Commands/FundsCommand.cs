using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;
using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Application.Commands;

public enum FundsCommandType
{
    Reserve = 1,
    Settle = 2,
    Release = 3
}

/// <summary>
/// A funds command from the Payments saga orchestrator (via accounts.commands). MessageId is
/// the command's own ID: the Inbox key and the CausationId of the reply.
/// </summary>
public sealed record FundsCommand(
    FundsCommandType Type,
    Guid MessageId,
    Guid PaymentRef,
    string PaymentNumber,
    string CustomerNumber,
    string Bsb,
    string AccountNumber,
    decimal Amount,
    string Currency,
    string? InitiatedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId);

public sealed record FundsCommandResult(string HoldStatus, bool AlreadyProcessed);

/// <summary>
/// Applies one funds command in ONE local transaction: the hold row and the account row
/// are locked, changed and saved together with the reply in the Outbox. Accounts decides
/// only about its own data ("is there enough money?") - never about the payment workflow.
///
/// Idempotent twice over: per message (Inbox) and per payment (one hold per PaymentRef;
/// a repeated command repeats the reply).
/// </summary>
public sealed class FundsCommandHandler(
    IAccountRepository accounts,
    IFundsHoldRepository holds,
    IInboxStore inbox,
    IAccountsUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<FundsCommandHandler> logger)
{
    public const string InboxConsumer = "accounts.funds-commands";

    public async Task<FundsCommandResult> HandleAsync(FundsCommand command, CancellationToken cancellationToken)
    {
        if (await inbox.HasProcessedAsync(command.MessageId, InboxConsumer, cancellationToken))
            return new FundsCommandResult("UNCHANGED", AlreadyProcessed: true);

        try
        {
            return await ApplyAsync(command, cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            // Two deliveries raced to create the same payment's hold: the loser repeats the
            // winner's answer.
            unitOfWork.DiscardChanges();
            return await ApplyAsync(command, cancellationToken);
        }
    }

    private async Task<FundsCommandResult> ApplyAsync(FundsCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var hold = await holds.GetForUpdateAsync(command.PaymentRef, cancellationToken);
        var account = hold?.AccountId is { } accountId
            ? await accounts.GetForUpdateAsync(accountId, cancellationToken)
            : null;

        switch (command.Type)
        {
            case FundsCommandType.Reserve when hold is not null:
                hold.RepeatReservation(now);
                break;

            case FundsCommandType.Reserve:
                account = await accounts.GetForUpdateAsync(command.Bsb, command.AccountNumber, cancellationToken);
                hold = account is null
                    ? FundsHold.RefuseUnknownAccount(command.PaymentRef, command.PaymentNumber, command.Bsb, command.AccountNumber,
                        command.CustomerNumber, command.Amount, command.Currency, command.InitiatedByUserId, now)
                    : FundsHold.Reserve(command.PaymentRef, command.PaymentNumber, account, command.CustomerNumber,
                        command.Amount, command.Currency, command.InitiatedByUserId, now);
                holds.Add(hold);
                break;

            case FundsCommandType.Settle when hold is null:
                throw new Domain.Exceptions.DomainConflictException(
                    $"Accounts holds no funds for payment {command.PaymentNumber}: there is nothing to settle.");

            case FundsCommandType.Settle:
                hold.Settle(account ?? throw new Domain.Exceptions.DomainConflictException("The held account no longer exists."), now);
                break;

            case FundsCommandType.Release when hold is null:
                hold = FundsHold.ReleaseBeforeReservation(command.PaymentRef, command.PaymentNumber, command.Bsb, command.AccountNumber,
                    command.CustomerNumber, command.Currency, command.InitiatedByUserId, now);
                holds.Add(hold);
                break;

            case FundsCommandType.Release:
                hold.Release(account, now);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Type, "Unknown funds command.");
        }

        inbox.RecordProcessed(command.MessageId, InboxConsumer);
        await unitOfWork.SaveChangesAsync(new WorkflowContext(command.WorkflowId, command.CorrelationId, command.MessageId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "{Command} funds for payment {PaymentNumber} ({Amount} {Currency}, {Bsb} {AccountNumber}): hold is {Status}{Reason}.",
            command.Type, command.PaymentNumber, command.Amount, command.Currency, command.Bsb, command.AccountNumber,
            hold.Status.ToCode(), hold.RefusalReason is { } r ? $" ({r.ToCode()})" : string.Empty);

        return new FundsCommandResult(hold.Status.ToCode(), AlreadyProcessed: false);
    }
}