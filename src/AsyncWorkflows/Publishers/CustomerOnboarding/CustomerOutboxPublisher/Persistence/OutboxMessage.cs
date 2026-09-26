namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Publishers.CustomerOnboarding.CustomerOutboxPublisher.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}