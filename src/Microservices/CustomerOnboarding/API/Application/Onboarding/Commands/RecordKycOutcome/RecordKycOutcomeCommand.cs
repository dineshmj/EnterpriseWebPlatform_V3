namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordKycOutcome;

/// <summary>
/// Applies a Customer KYC fact (KycCaseCreated / KycCaseApproved / KycCaseRejected)
/// to an onboarding application, identified by its cross-context ApplicationRef.
/// MessageId is the KYC event's MessageId and is the idempotency key.
/// ApplicationNumber is a consistency check: it must match the referenced application.
/// </summary>
public sealed record RecordKycOutcomeCommand(
    Guid ApplicationRef,
    string ApplicationNumber,
    Guid MessageId,
    string EventType);

public enum RecordKycOutcomeResult
{
    /// <summary>The application changed state.</summary>
    Applied,

    /// <summary>Valid fact, but the application was already at or beyond it.</summary>
    NoChange,

    /// <summary>This message was processed before (redelivery).</summary>
    Duplicate,

    NotFound,

    /// <summary>The referenced application has a different application number (inconsistent fact).</summary>
    ApplicationMismatch,

    /// <summary>The event type is not a KYC outcome this context understands.</summary>
    UnsupportedEventType
}
