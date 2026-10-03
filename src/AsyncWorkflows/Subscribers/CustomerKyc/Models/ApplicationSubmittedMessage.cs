namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Models;

/// <summary>
/// The subscriber's own (tolerant-reader) view of the
/// onboarding.application.submitted integration event: only the fields KYC needs.
/// </summary>
public sealed record ApplicationSubmittedMessage(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    long ApplicationId,
    string ApplicationNumber,
    string CustomerNumber,
    string? InitiatedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId,
    Guid? CausationId,
    string? Source);
