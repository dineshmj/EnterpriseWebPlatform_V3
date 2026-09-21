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
        DateTimeOffset occurredAt)
    {
        Id = id;
        AggregateType = aggregateType;
        AggregateId = aggregateId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    public string AggregateType { get; private set; } = string.Empty;

    public string AggregateId { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public JsonDocument Payload { get; private set; } = null!;

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create(
        string aggregateType,
        string aggregateId,
        string eventType,
        JsonDocument payload,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);

        return new OutboxMessage(
            Guid.NewGuid(),
            aggregateType,
            aggregateId,
            eventType,
            payload,
            occurredAt);
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
