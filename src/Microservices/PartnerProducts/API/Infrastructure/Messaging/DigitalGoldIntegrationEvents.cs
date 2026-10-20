using System.Text.Json;

using EnterpriseWebPlatform.Common.Observability;
using EnterpriseWebPlatform.PartnerProducts.Api.Domain;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure.Messaging;

// Published contracts (topic partner-products.digital-gold.events), in the platform's standard envelope.

public sealed record DigitalGoldIntegrationEventEnvelope<TPayload>(
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

public sealed record GoldAccountApplicationPayload(
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string FormNumber,
    string NewStatus,
    string? PartnerAccountNumber,
    bool Resend,
    string? Reason);

public sealed record GoldPurchasePayload(
    string PurchaseNumber,
    string CustomerNumber,
    string BranchCode,
    string SlipNumber,
    string NewStatus,
    string PartnerAccountNumber,
    string? PartnerOrderNumber,
    decimal Amount,
    string Currency,
    decimal? Grams,
    decimal? PricePerGram,
    string? PaymentNumber,
    bool Resend,
    string? Reason);

/// <summary>Who caused the change: the staff member (delegated token) or, for the workflow's steps, the worker's machine client.</summary>
public sealed record EventActor(string? UserId, string? LanId);

/// <summary>
/// Translates the aggregates' Digital Gold events into Outbox rows: the single place where the
/// domain's language meets the public contract.
/// </summary>
internal static class DigitalGoldIntegrationEventMapper
{
    public const string ApplicationAggregate = "GoldAccountApplication";
    public const string PurchaseAggregate = "GoldPurchase";

    private const string Source = "partner-products";
    private const int SchemaVersion = 1;

    // ┌─ :: SECURITY :: Data minimisation in published events ─────────────────────────────────────────
    // │ Why : Events are read by every authorised consumer and kept by the tamper-evident audit trail
    // │       for good; the customer's name, date of birth and mobile are not needed there.
    // │ How : Only identifiers (customer, Form VII, slip, purchase, Partner Bank account / order,
    // │       payment), the branch, the status, the amount and the Partner Bank's reason; the people
    // │       who acted are in the envelope (subject and LAN ID, from the token - never the body).
    // │ Ref : Integration-Event-Catalogue.md | Privacy Act APP 11 | OWASP API3
    // └────────────────────────────────────────────────────────────────────────────────────────────────
    public static OutboxMessage ToOutboxMessage(GoldAccountApplication application, DigitalGoldEvent raised, EventActor actor) =>
        Message(ApplicationAggregate, application.Number, raised, actor, correlationId: null,
            new GoldAccountApplicationPayload(
                application.Number,
                application.CustomerNumber,
                application.BranchCode,
                application.FormNumber,
                application.Status,
                application.PartnerAccountNumber,
                raised.Resend,
                raised.Reason));

    // A purchase's events share its payment key as the CorrelationId: one purchase, one business transaction.
    public static OutboxMessage ToOutboxMessage(GoldPurchase purchase, DigitalGoldEvent raised, EventActor actor) =>
        Message(PurchaseAggregate, purchase.Number, raised, actor, purchase.PaymentRef,
            new GoldPurchasePayload(
                purchase.Number,
                purchase.CustomerNumber,
                purchase.BranchCode,
                purchase.SlipNumber,
                purchase.Status,
                purchase.PartnerAccountNumber,
                purchase.PartnerOrderNumber,
                purchase.AmountAud,
                "AUD",
                purchase.Grams,
                purchase.PricePerGram,
                purchase.PaymentNumber,
                raised.Resend,
                raised.Reason));

    private static OutboxMessage Message<TPayload>(
        string aggregateType, string aggregateId, DigitalGoldEvent raised, EventActor actor, Guid? correlationId, TPayload payload)
    {
        var messageId = Guid.NewGuid();
        var envelope = new DigitalGoldIntegrationEventEnvelope<TPayload>(
            messageId, raised.EventType, SchemaVersion, Source, raised.OccurredAt,
            WorkflowId: null, correlationId, CausationId: null, actor.UserId, payload, actor.LanId);

        return new OutboxMessage
        {
            Id = messageId,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            EventType = raised.EventType,
            Payload = JsonSerializer.Serialize(envelope),
            OccurredAt = raised.OccurredAt,
            CorrelationId = correlationId,
            InitiatedByUserId = actor.UserId,
            ActedByUserId = actor.UserId,
            TraceParent = MessagingTelemetry.CurrentTraceParent()
        };
    }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? WorkflowId { get; set; }
    public Guid? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public string? InitiatedByUserId { get; set; }
    public string? ActedByUserId { get; set; }

    /// <summary>W3C trace context of the request that raised the event.</summary>
    public string? TraceParent { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}