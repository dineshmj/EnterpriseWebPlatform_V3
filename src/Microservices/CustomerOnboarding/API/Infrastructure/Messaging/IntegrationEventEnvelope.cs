namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

public sealed record IntegrationEventEnvelope<TPayload>(
    Guid MessageId,
    string EventType,
    string Source,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string? CausationId,
    TPayload Payload);
