using System.Text.Json;

namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;

public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    private OutboxMessage(
        Guid id,
        string aggregateType,
        string aggregateId,
        string eventType,
        JsonDocument payload,
        DateTimeOffset occurredAt,
        Guid? workflowId,
        Guid? correlationId,
        Guid? causationId,
        Guid? initiatedByUserId,
        string? traceParent)
    {
        Id = id;
        AggregateType = aggregateType;
        AggregateId = aggregateId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;
        WorkflowId = workflowId;
        CorrelationId = correlationId;
        CausationId = causationId;
        InitiatedByUserId = initiatedByUserId;
        TraceParent = traceParent;
    }

    public Guid Id { get; private set; }

    /// <summary>Database-assigned insertion order; defines publication order per aggregate.</summary>
    public long Sequence { get; private set; }

    public string AggregateType { get; private set; } = string.Empty;

    public string AggregateId { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public JsonDocument Payload { get; private set; } = null!;

    public DateTimeOffset OccurredAt { get; private set; }

    public Guid? WorkflowId { get; private set; }

    public Guid? CorrelationId { get; private set; }

    public Guid? CausationId { get; private set; }

    public Guid? InitiatedByUserId { get; private set; }

    /// <summary>
    /// W3C trace context of the request that raised the event; the relay continues
    /// this trace and sends it as the Kafka "traceparent" header.
    /// </summary>
    public string? TraceParent { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    /// <param name="messageId">
    /// The integration event's MessageId. The Outbox row uses the same identifier,
    /// so a row, its Kafka message and any CausationId pointing at it all agree.
    /// </param>
    public static OutboxMessage Create(
        Guid messageId,
        string aggregateType,
        string aggregateId,
        string eventType,
        JsonDocument payload,
        DateTimeOffset occurredAt,
        Guid? workflowId,
        Guid? correlationId,
        Guid? causationId,
        Guid? initiatedByUserId,
        string? traceParent = null)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("A message ID is required.", nameof(messageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        return new OutboxMessage(
            messageId,
            aggregateType,
            aggregateId,
            eventType,
            payload,
            occurredAt,
            workflowId,
            correlationId,
            causationId,
            initiatedByUserId,
            traceParent);
    }

    public void MarkPublished(DateTimeOffset publishedAt)
    {
        PublishedAt = publishedAt;
    }

    public void RecordPublishFailure(
        DateTimeOffset attemptedAt,
        string error)
    {
        AttemptCount++;
        LastAttemptAt = attemptedAt;
        LastError = error;
    }
}