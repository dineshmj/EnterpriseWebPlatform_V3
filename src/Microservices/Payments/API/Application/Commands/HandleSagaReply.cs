using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Application.Commands;

/// <summary>A reply from Accounts (accounts.funds.replies), as the reply courier hands it over.</summary>
public sealed record SagaReplyCommand(
    Guid MessageId,
    string ReplyType,
    Guid PaymentRef,
    string? ReasonCode,
    string? Reason,
    bool NothingWasHeld);

public enum SagaReplyOutcome
{
    Applied,
    AlreadyProcessed
}

/// <summary>
/// Hands one reply to the payment's saga: locks the saga, lets it decide, and saves the
/// saga, the payment and any next command in ONE transaction (with the Inbox record).
/// The saga ignores a reply that is late or duplicated.
/// </summary>
public sealed class HandleSagaReplyCommandHandler(
    IPaymentRepository payments,
    IPaymentSagaRepository sagas,
    IInboxStore inbox,
    IPaymentsUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<HandleSagaReplyCommandHandler> logger)
{
    public const string InboxConsumer = "payments.saga-replies";

    public async Task<SagaReplyOutcome> HandleAsync(SagaReplyCommand reply, CancellationToken cancellationToken)
    {
        if (await inbox.HasProcessedAsync(reply.MessageId, InboxConsumer, cancellationToken))
            return SagaReplyOutcome.AlreadyProcessed;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var saga = await sagas.GetForUpdateAsync(reply.PaymentRef, cancellationToken)
            ?? throw new DomainRuleViolationException($"No payment saga exists for PaymentRef {reply.PaymentRef}.");
        var payment = await payments.GetAsync(saga.PaymentId, cancellationToken);
        var now = clock.GetUtcNow();

        switch (reply.ReplyType)
        {
            case "FundsReserved":
                saga.OnFundsReserved(payment, reply.MessageId, now);
                break;
            case "FundsReservationFailed":
                saga.OnFundsReservationFailed(payment, reply.MessageId, reply.ReasonCode ?? "FUNDS_NOT_RESERVED",
                    reply.Reason ?? "Accounts could not reserve the funds.", now);
                break;
            case "FundsSettled":
                saga.OnFundsSettled(payment, reply.MessageId, now);
                break;
            case "FundsReleased":
                saga.OnFundsReleased(payment, reply.MessageId, reply.NothingWasHeld, now);
                break;
            default:
                throw new DomainRuleViolationException($"Unknown reply type '{reply.ReplyType}'.");
        }

        inbox.RecordProcessed(reply.MessageId, InboxConsumer);
        await unitOfWork.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Saga {SagaId} of payment {PaymentNumber}: {ReplyType} handled; now at {Step} ({SagaStatus}), payment {PaymentStatus}.",
            saga.Id, payment.PaymentNumber, reply.ReplyType, saga.Step.ToCode(), saga.Status.ToCode(), payment.Status.ToCode());

        return SagaReplyOutcome.Applied;
    }
}