using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Payments.Api.Application.Commands;

public sealed record ResendParkedMessageCommand(Guid MessageId, string OperatorLabel, string Reason);

public sealed record ResendParkedMessageResult(Guid MessageId, string EventType, string? PaymentNumber, int RedriveCount);

// ┌─ :: ARCHITECTURE + SECURITY :: Resend a parked Outbox message ─────────────────────────────────
// │ Why : A message parked after Kafka was down for long would block the payment for good; fixing
// │       it by hand in the database would be unrecorded and could duplicate it.
// │ How : Operations resend the SAME message (same ID) with a reason, workflow.reprocess and a
// │       recent MFA code; consumers' Inboxes drop a copy that did arrive; who, when and why are
// │       kept on the message and the timeline.
// │ Ref : Features 1.6.2 | Threat-Model R-5 | Transactional Outbox pattern
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Operations recovery for a PARKED Outbox message (the relay gave up after its bounded
/// attempts, e.g. Kafka was down for long): the same message - same id, same payload - goes
/// back to the relay with fresh attempts, and the payment's timeline records who resent it and
/// why (message, saga and timeline saved in ONE transaction). Consumers deduplicate by message
/// id in their Inboxes, so a message that did arrive earlier is not acted on twice.
/// </summary>
public sealed class ResendParkedMessageCommandHandler(
    IParkedOutbox outbox,
    IPaymentSagaRepository sagas,
    IPaymentsUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ResendParkedMessageCommandHandler> logger)
{
    public const int MinReasonLength = 10;
    public const int MaxReasonLength = 500;

    /// <summary>Null when the message does not exist.</summary>
    public async Task<ResendParkedMessageResult?> HandleAsync(ResendParkedMessageCommand command, CancellationToken cancellationToken)
    {
        var reason = command.Reason.Trim();
        if (reason.Length is < MinReasonLength or > MaxReasonLength)
            throw new ArgumentException($"A reason of {MinReasonLength} to {MaxReasonLength} characters is required.");

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var aggregateId = await outbox.FindAggregateIdAsync(command.MessageId, cancellationToken);
        if (aggregateId is null)
            return null;

        // Saga first, then the message: the same lock order as every other change to a payment.
        var saga = Guid.TryParse(aggregateId, out var paymentRef)
            ? await sagas.GetForUpdateAsync(paymentRef, cancellationToken)
            : null;

        var message = await outbox.LockParkedAsync(command.MessageId, cancellationToken)
            ?? throw new DomainConflictException("The message is not parked: the relay is still trying it, or it was already sent.");

        var now = clock.GetUtcNow();
        await outbox.ResendAsync(message.MessageId, command.OperatorLabel, reason, now, cancellationToken);
        saga?.RecordMessageResent(message.MessageId, message.EventType, command.OperatorLabel, reason, now);

        await unitOfWork.SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning("Operations ({Operator}) resent parked Payments outbox message {MessageId} {EventType} for payment {PaymentNumber}. Reason: {Reason}",
            command.OperatorLabel, message.MessageId, message.EventType, message.PaymentNumber, reason);
        return new ResendParkedMessageResult(message.MessageId, message.EventType, message.PaymentNumber, message.RedriveCount + 1);
    }
}