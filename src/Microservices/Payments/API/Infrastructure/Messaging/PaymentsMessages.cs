using System.Text.Json;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Common;
using EnterpriseWebPlatform.Payments.Api.Domain.Events;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Infrastructure.Persistence;

namespace EnterpriseWebPlatform.Payments.Api.Infrastructure.Messaging;

/// <summary>
/// The platform's standard envelope. For a COMMAND (to accounts.commands) EventType holds
/// the command name (ReserveFunds, SettleFunds, ReleaseFunds): one envelope shape for every
/// message keeps the Outbox, the relay and the couriers identical.
/// </summary>
public sealed record PaymentsMessageEnvelope<TPayload>(
    Guid MessageId,
    string EventType,
    int SchemaVersion,
    string Source,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid? CausationId,
    string? InitiatedByUserId,
    TPayload Payload,
    string? InitiatedByLanId = null);

/// <summary>What Accounts needs to reserve, settle or release the payment's funds.</summary>
public sealed record FundsCommandPayload(
    Guid PaymentRef,
    string PaymentNumber,
    string CustomerNumber,
    string Bsb,
    string AccountNumber,
    decimal Amount,
    string Currency);

/// <summary>A payment's outcome (payments.payment.events).</summary>
public sealed record PaymentOutcomePayload(
    long PaymentId,
    Guid PaymentRef,
    string PaymentNumber,
    string CustomerNumber,
    string BranchCode,
    string Status,
    decimal Amount,
    string Currency,
    string FromBsb,
    string FromAccountNumber,
    string PayeeName,
    string ToBsb,
    string ToAccountNumber,
    string? NetworkReference,
    string? ReasonCode,
    string? Reason,
    bool ApprovalRequired,
    string? DecisionByUserId,
    string? DecisionByLanId,
    string? DecisionRemarks);

/// <summary>
/// Translates the saga's commands and the payment's facts into Outbox rows: the single
/// place where the domain's language meets the published contracts. One aggregate stream
/// per payment (type Payment, ID = PaymentRef, which is also the Kafka key), so all of a
/// payment's messages leave in the order they were written.
/// </summary>
internal static class PaymentsMessageMapper
{
    public const string AggregateType = "Payment";
    public const string ReserveFunds = PaymentSaga.ReserveFundsCommand;
    public const string SettleFunds = PaymentSaga.SettleFundsCommand;
    public const string ReleaseFunds = PaymentSaga.ReleaseFundsCommand;
    public const string PaymentApprovalRequired = "PaymentApprovalRequired";
    public const string PaymentCompleted = "PaymentCompleted";
    public const string PaymentRejected = "PaymentRejected";
    public const string PaymentFailed = "PaymentFailed";
    public const string PaymentCompensationFailed = "PaymentCompensationFailed";

    private const string Source = "payments";
    private const int SchemaVersion = 1;

    public static OutboxMessage ToOutboxMessage(PaymentSaga saga, Payment payment, IDomainEvent domainEvent, Func<string?, string?> lanOf)
    {
        var initiatorLanId = lanOf(saga.InitiatedByUserId);
        Guid messageId;
        string eventType;
        string payload;
        Guid? causationId;

        switch (domainEvent)
        {
            case FundsCommandIssuedDomainEvent command:
                messageId = command.MessageId;   // the saga waits for the reply to THIS message
                eventType = command.CommandType;
                causationId = command.CausationId;
                payload = Envelope(messageId, eventType, command.OccurredAt, saga, causationId, initiatorLanId,
                    new FundsCommandPayload(command.PaymentRef, command.PaymentNumber, command.CustomerNumber,
                        command.Bsb, command.AccountNumber, command.Amount, command.Currency));
                break;

            case PaymentApprovalRequiredDomainEvent waiting:
                (messageId, eventType, causationId) = (Guid.NewGuid(), PaymentApprovalRequired, saga.LastMessageId);
                payload = Envelope(messageId, eventType, waiting.OccurredAt, saga, causationId, initiatorLanId, Outcome(payment, null, null, lanOf));
                break;

            case PaymentCompletedDomainEvent completed:
                (messageId, eventType, causationId) = (Guid.NewGuid(), PaymentCompleted, saga.LastMessageId);
                payload = Envelope(messageId, eventType, completed.OccurredAt, saga, causationId, initiatorLanId, Outcome(payment, null, null, lanOf));
                break;

            case PaymentRejectedDomainEvent rejected:
                (messageId, eventType, causationId) = (Guid.NewGuid(), PaymentRejected, saga.LastMessageId);
                payload = Envelope(messageId, eventType, rejected.OccurredAt, saga, causationId, initiatorLanId,
                    Outcome(payment, rejected.ReasonCode, rejected.Reason, lanOf));
                break;

            case PaymentFailedDomainEvent failed:
                (messageId, eventType, causationId) = (Guid.NewGuid(), PaymentFailed, saga.LastMessageId);
                payload = Envelope(messageId, eventType, failed.OccurredAt, saga, causationId, initiatorLanId,
                    Outcome(payment, failed.ReasonCode, failed.Reason, lanOf));
                break;

            case PaymentCompensationFailedDomainEvent stuck:
                (messageId, eventType, causationId) = (Guid.NewGuid(), PaymentCompensationFailed, saga.CurrentCommandId);
                payload = Envelope(messageId, eventType, stuck.OccurredAt, saga, causationId, initiatorLanId,
                    Outcome(payment, "COMPENSATION_FAILED", stuck.Reason, lanOf));
                break;

            default:
                throw new InvalidOperationException($"No message mapping exists for domain event {domainEvent.GetType().Name}.");
        }

        return new OutboxMessage
        {
            Id = messageId,
            AggregateType = AggregateType,
            AggregateId = payment.PaymentRef.ToString(),
            EventType = eventType,
            Payload = payload,
            OccurredAt = domainEvent.OccurredAt,
            WorkflowId = saga.WorkflowId,
            CorrelationId = saga.CorrelationId,
            CausationId = causationId,
            InitiatedByUserId = saga.InitiatedByUserId,
            TraceParent = MessagingTelemetry.CurrentTraceParent()
        };
    }

    private static PaymentOutcomePayload Outcome(Payment payment, string? reasonCode, string? reason, Func<string?, string?> lanOf) =>
        new(payment.Id, payment.PaymentRef, payment.PaymentNumber, payment.CustomerNumber, payment.BranchCode.Value,
            payment.Status.ToCode(), payment.Amount, payment.Currency, payment.From.Bsb, payment.From.AccountNumber,
            payment.PayeeName, payment.To.Bsb, payment.To.AccountNumber, payment.NetworkReference, reasonCode, reason,
            payment.ApprovalRequired, payment.DecisionByUserId, lanOf(payment.DecisionByUserId), payment.DecisionRemarks);

    private static string Envelope<TPayload>(
        Guid messageId, string eventType, DateTimeOffset occurredAt, PaymentSaga saga, Guid? causationId,
        string? initiatorLanId, TPayload payload) =>
        JsonSerializer.Serialize(new PaymentsMessageEnvelope<TPayload>(
            messageId, eventType, SchemaVersion, Source, occurredAt, saga.WorkflowId, saga.CorrelationId, causationId,
            saga.InitiatedByUserId, payload, initiatorLanId));
}