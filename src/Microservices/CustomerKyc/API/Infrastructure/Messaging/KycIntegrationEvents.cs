namespace EnterpriseWebPlatform.CustomerKyc.Api.Infrastructure.Messaging;

// Published contracts (topics kyc.*). These shapes are what consumers read; the
// domain events are internal and translated to these by KycIntegrationEventMapper.

public sealed record KycCaseCreatedEvent(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    long KycCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string BranchCode,
    string Status,
    string? InitiatedByUserId);

public sealed record KycVerificationStageDecisionEvent(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    long KycCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string Stage,   // "IDENTITY_VERIFICATION" / "DOCUMENT_VERIFICATION" - a stable code, not an enum ordinal
    string PreviousStageStatus,
    string NewStageStatus,
    string? InitiatedByUserId,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks,
    string OverallStatus);

public sealed record KycCaseDecisionEvent(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid CausationId,
    long KycCaseId,
    Guid ApplicationRef,
    string ApplicationNumber,
    string CustomerNumber,
    string PreviousStatus,
    string NewStatus,
    string? InitiatedByUserId,
    string DecisionByUserId,
    DateTimeOffset DecisionAt,
    string? DecisionRemarks,
    IReadOnlyList<Guid> CausedByMessageIds);