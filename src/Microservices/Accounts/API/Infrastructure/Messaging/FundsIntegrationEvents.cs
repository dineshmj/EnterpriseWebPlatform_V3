using System.Text.Json;

using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Common;
using EnterpriseWebPlatform.Accounts.Api.Domain.Events;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Infrastructure.Persistence;
using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.Accounts.Api.Infrastructure.Messaging;

/// <summary>
/// The reply to a funds command (topic accounts.funds.replies, key = PaymentRef). The
/// envelope's CausationId is the command's MessageId, and WorkflowId / CorrelationId are
/// the command's, so the reply belongs to the payment's workflow.
/// </summary>
public sealed record FundsReplyPayload(
    Guid PaymentRef,
    string PaymentNumber,
    string HoldStatus,
    string Bsb,
    string AccountNumber,
    decimal Amount,
    string Currency,
    string? Reason,
    string? ReasonText,
    bool? NothingWasHeld);

/// <summary>
/// Translates FundsHold domain events into replies (Outbox rows). Every funds event is a
/// reply: Accounts answers each command, including a repeated one.
/// </summary>
internal static class FundsIntegrationEventMapper
{
    public const string AggregateType = "FundsHold";
    public const string FundsReserved = "FundsReserved";
    public const string FundsReservationFailed = "FundsReservationFailed";
    public const string FundsSettled = "FundsSettled";
    public const string FundsReleased = "FundsReleased";

    private const string Source = "accounts";
    private const int SchemaVersion = 1;

    public static OutboxMessage ToOutboxMessage(
        FundsHold hold, IDomainEvent domainEvent, Guid? workflowId, Guid? correlationId, Guid causationId)
    {
        var (eventType, reason, nothingWasHeld) = domainEvent switch
        {
            FundsReservedDomainEvent => (FundsReserved, (FundsRefusalReason?)null, (bool?)null),
            FundsReservationRefusedDomainEvent refused => (FundsReservationFailed, refused.Reason, null),
            FundsSettledDomainEvent => (FundsSettled, null, null),
            FundsReleasedDomainEvent released => (FundsReleased, null, released.NothingWasHeld),
            _ => throw new InvalidOperationException($"No reply mapping exists for domain event {domainEvent.GetType().Name}.")
        };

        var messageId = Guid.NewGuid();
        var payload = new FundsReplyPayload(
            hold.PaymentRef,
            hold.PaymentNumber,
            hold.Status.ToCode(),
            hold.Bsb,
            hold.AccountNumber,
            hold.Amount,
            hold.Currency,
            reason?.ToCode(),
            reason?.Describe(),
            nothingWasHeld);

        return new OutboxMessage
        {
            Id = messageId,
            AggregateType = AggregateType,
            AggregateId = hold.PaymentRef.ToString(),
            EventType = eventType,
            Payload = JsonSerializer.Serialize(new AccountsIntegrationEventEnvelope<FundsReplyPayload>(
                messageId, eventType, SchemaVersion, Source, domainEvent.OccurredAt, workflowId, correlationId, causationId,
                hold.InitiatedByUserId, payload)),
            OccurredAt = domainEvent.OccurredAt,
            WorkflowId = workflowId,
            CorrelationId = correlationId,
            CausationId = causationId,
            InitiatedByUserId = hold.InitiatedByUserId,
            TraceParent = MessagingTelemetry.CurrentTraceParent()
        };
    }
}