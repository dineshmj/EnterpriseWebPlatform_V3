namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerOnboarding.KycSubscriber.Messages;

/// <summary>
/// Tolerant-reader view of the Customer KYC case events (kyc.case.created /
/// approved / rejected): only the fields Customer Onboarding needs. Unknown
/// fields are ignored, so KYC can add fields without breaking this consumer.
/// </summary>
public sealed record KycOutcomeMessage(
    Guid MessageId,
    string EventType,
    long KycCaseId,
    Guid ApplicationRef,
    string? ApplicationNumber,
    Guid? WorkflowId,
    Guid? CorrelationId,
    string? InitiatedByUserId);
