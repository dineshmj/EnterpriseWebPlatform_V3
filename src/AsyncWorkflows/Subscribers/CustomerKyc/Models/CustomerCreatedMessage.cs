namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Models;

public sealed record CustomerCreatedMessage(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    string CustomerNumber,
    string? InitiatedByUserId,
    string? CorrelationId,
    string? CausationId,
    string? Source);
