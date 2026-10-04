namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure;

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