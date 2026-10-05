namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

/// <summary>
/// The standard envelope (doc/Integration-Event-Catalogue.md §3). SchemaVersion is
/// the contract version of the payload; it changes only for a breaking change.
/// </summary>
public sealed record IntegrationEventEnvelope<TPayload>(
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